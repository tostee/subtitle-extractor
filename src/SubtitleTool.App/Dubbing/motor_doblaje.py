"""Motor de doblaje de Subtítulos: subtítulos -> voces -> pista de audio doblada dentro de un .mkv.

Lo lanza la aplicación con el Python del motor base (edge-tts):
    python motor_doblaje.py plan.json

El plan (JSON, lo escribe la app) describe el video, las líneas (texto, tiempos, personaje, emoción),
la voz de cada personaje y los motores opcionales (IndexTTS-2.5 para emociones, Seed-VC para voces propias).
El progreso sale por stdout como una línea JSON por evento:
    {"fase": "voces", "hecho": 3, "total": 40, "mensaje": "..."}
    {"fase": "fin", "salida": "...", "informe": {...}}
    {"fase": "error", "mensaje": "..."}
Los audios intermedios se guardan en work_dir con un nombre que depende de su contenido: si se cancela y se
vuelve a lanzar, lo ya generado se reutiliza.
"""
import asyncio
import hashlib
import json
import os
import re
import subprocess
import sys
import wave

sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # la app lee UTF-8 (con -I se ignora PYTHONIOENCODING)

SR = 48000          # pista de voces: mono, 48 kHz, 16 bits
GAP = 0.08          # separación mínima entre dos líneas (s)
MAX_TEMPO = 1.6     # aceleración máxima con atempo
REGEN_RATIO = 1.5   # IndexTTS: si no cabe ni acelerando esto, se regenera hablando más rápido
TRIM = ("silenceremove=start_periods=1:start_threshold=-45dB,areverse,"
        "silenceremove=start_periods=1:start_threshold=-45dB,areverse")
EMO_ORDER = ["alegria", "ira", "tristeza", "miedo", "asco", "melancolia", "sorpresa", "calma"]
NO_WINDOW = 0x08000000 if os.name == "nt" else 0


def emit(fase, **kw):
    print(json.dumps(dict(fase=fase, **kw), ensure_ascii=False), flush=True)


def run(*args):
    r = subprocess.run(args, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, creationflags=NO_WINDOW)
    if r.returncode != 0:
        err = r.stderr.decode("utf-8", "replace").strip().splitlines()
        raise RuntimeError(f"{os.path.basename(args[0])} falló: " + (err[-1] if err else f"código {r.returncode}"))


def key(*parts):
    return hashlib.sha1(json.dumps(parts, ensure_ascii=False, sort_keys=True).encode("utf-8")).hexdigest()[:16]


def para_voz(text):
    """Texto para la voz: sin etiquetas, sin saltos y sin los apóstrofos del habla escrita (pa', pa'l, mi'jo),
    que el TTS lee como fin de frase y mete una pausa."""
    text = re.sub(r"\{[^}]*\}", "", text)
    text = re.sub(r"<[^>]+>", "", text).replace("\n", " ")
    text = re.sub(r"^\s*-\s*", "", text)
    text = re.sub(r"\s+-\s+", " ", text)
    text = re.sub(r"(\w)['’](\w)", r"\1\2", text)
    text = re.sub(r"(\w)['’]", r"\1", text)
    text = re.sub(r"\[[^\]]*\]|\([^)]*\)|♪[^♪]*♪?", "", text)  # efectos de sonido y canciones no se leen
    return re.sub(r"\s+", " ", text).strip()


class Motor:
    def __init__(self, plan):
        self.p = plan
        self.ffmpeg = plan["ffmpeg"]
        self.ffprobe = plan["ffprobe"]
        self.work = plan["work_dir"]
        for d in ("tts", "vc", "final"):
            os.makedirs(os.path.join(self.work, d), exist_ok=True)
        rng = plan.get("range")
        self.t0 = float(rng["start"]) if rng else 0.0
        self.t1 = float(rng["end"]) if rng else self.probe_duration(plan["video"])
        self.lines = []
        for ln in plan["lines"]:
            if ln["end"] <= self.t0 or ln["start"] >= self.t1:
                continue
            text = para_voz(ln["text"])
            if not text or not re.search(r"\w", text):
                continue
            self.lines.append(dict(ln, voz=text, s=max(ln["start"], self.t0) - self.t0, e=min(ln["end"], self.t1) - self.t0))
        self.lines.sort(key=lambda x: x["s"])

    # ---------- utilidades de audio ----------
    def probe_duration(self, path):
        out = subprocess.run([self.ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
                             capture_output=True, text=True, creationflags=NO_WINDOW).stdout.strip()
        try:
            return float(out)
        except ValueError:
            raise RuntimeError(f"ffprobe no pudo leer la duración de {os.path.basename(path)}.")

    def trimmed(self, src, dst, sr=SR):
        """Copia sin silencio al principio ni al final, mono (48 kHz salvo que se pida otra frecuencia)."""
        if not os.path.exists(dst):
            run(self.ffmpeg, "-y", "-v", "error", "-i", src, "-af", TRIM, "-ac", "1", "-ar", str(sr), dst + ".tmp.wav")
            os.replace(dst + ".tmp.wav", dst)
        return dst

    def voice(self, ln):
        return self.p["voices"].get(ln.get("speaker") or "", self.p["default_voice"])

    # ---------- 1) texto a voz ----------
    def tts_edge(self, todo):
        import edge_tts
        sem = asyncio.Semaphore(6)
        done = [0]

        async def one(ln):
            v = ln["_v"]
            tmp = ln["_raw"] + ".tmp.mp3"
            async with sem:
                for intento in range(4):
                    try:
                        await edge_tts.Communicate(ln["voz"], v["edge_voice"], rate=v["rate"], pitch=v["pitch"]).save(tmp)
                        break
                    except Exception as ex:  # red inestable: se reintenta
                        if intento == 3:
                            if type(ex).__name__ == "NoAudioReceived":
                                # Texto que la voz no sabe leer: se salta esa línea, no la película entera.
                                ln["_skip"] = True
                                emit("aviso", mensaje=f"La línea {ln['id']} no tiene audio (se deja en silencio).")
                                break
                            raise RuntimeError(f"edge-tts falló en la línea {ln['id']}: {ex}")
                        await asyncio.sleep(2 * (intento + 1))
            if not ln.get("_skip"):
                os.replace(tmp, ln["_raw"])
            done[0] += 1
            emit("voces", hecho=done[0], total=len(todo), mensaje=f"Voz de la línea {ln['id']}")

        async def main():
            await asyncio.gather(*(one(ln) for ln in todo))
        asyncio.run(main())

    def tts_indextts(self, todo, factors=None):
        cfg = self.p["indextts"]
        jobs = []
        for ln in todo:
            emo = [float(ln.get("emotion_vector", {}).get(n, 0.0)) for n in EMO_ORDER]
            jobs.append(dict(text=ln["voz"], speaker=ln["_v"]["accent_ref"], emo=emo, output=ln["_raw"],
                             duration_factor=(factors or {}).get(ln["id"], 1.0)))
        self.batch(cfg["python"], cfg["script"], cfg["repo"], jobs, "voces", extra_env={"EMO_ALPHA": str(cfg.get("alpha", 1.0))})

    def batch(self, python, script, repo, jobs, fase, extra_env=None):
        path = os.path.join(self.work, f"{fase}_jobs.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(jobs, f, ensure_ascii=False)
        env = dict(os.environ, PYTHONIOENCODING="utf-8", **(extra_env or {}))
        env.update(self.p.get("env", {}))
        proc = subprocess.Popen([python, script, repo, path], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                env=env, creationflags=NO_WINDOW, text=True, encoding="utf-8", errors="replace")
        tail = []

        def pump_err():
            for line in proc.stderr:
                tail.append(line.rstrip())
                del tail[:-40]
        import threading
        t = threading.Thread(target=pump_err, daemon=True)
        t.start()
        for line in proc.stdout:
            line = line.strip()
            if line.startswith("{"):
                try:
                    ev = json.loads(line)
                    emit(fase, hecho=ev.get("hecho", 0), total=ev.get("total", len(jobs)), mensaje=ev.get("mensaje", ""))
                except ValueError:
                    pass
        proc.wait()
        t.join(timeout=5)
        if proc.returncode != 0:
            detalle = next((l for l in reversed(tail) if l.strip()), f"código {proc.returncode}")
            raise RuntimeError(f"{os.path.basename(script)} falló: {detalle}")

    def accent_refs(self):
        """IndexTTS clona la voz de una muestra: se usa la propia voz cubana de edge-tts para conservar el acento."""
        import edge_tts
        texto = ("Oye, asere, ¿qué volá contigo? Ayer fui pa la playa con los socios, cogimos la guagua temprano y, "
                 "compadre, aquello estaba tremendo. Jamamos, gozamos, y después nos fuimos pa la casa caminando.")
        refs = {}
        for v in {ln["_v"]["edge_voice"] for ln in self.lines}:
            raw = os.path.join(self.work, f"acento_{v}.mp3")
            if not os.path.exists(raw):
                asyncio.run(edge_tts.Communicate(texto, v).save(raw + ".tmp"))
                os.replace(raw + ".tmp", raw)
            # A 24 kHz: con una muestra a 48 kHz IndexTTS tarda ~10 veces más en procesarla (medido: 230 s frente a 24 s).
            refs[v] = self.trimmed(raw, raw[:-4] + "_24k.wav", sr=24000)
        for ln in self.lines:
            ln["_v"] = dict(ln["_v"], accent_ref=refs[ln["_v"]["edge_voice"]], accent_ref_key=ln["_v"]["edge_voice"])

    def generate_voices(self):
        engine = self.p["engine"]
        for ln in self.lines:
            ln["_v"] = self.voice(ln)
        if engine == "indextts":
            self.accent_refs()
        for ln in self.lines:
            v = ln["_v"]
            if engine == "indextts":
                k = key("idx", ln["voz"], v["accent_ref_key"], ln.get("emotion_vector", {}), self.p["indextts"].get("alpha", 1.0))
            else:
                pitch = "+0Hz" if v.get("sample") and self.p.get("seedvc") else v["pitch"]
                v = ln["_v"] = dict(v, pitch=pitch)
                k = key("edge", ln["voz"], v["edge_voice"], v["rate"], pitch)
            ln["_key"] = k
            ln["_raw"] = os.path.join(self.work, "tts", k + (".wav" if engine == "indextts" else ".mp3"))
        # Un audio por clave: líneas iguales con la misma voz («No.», «¡Vamos!») se generan una sola vez.
        todo = list({ln["_raw"]: ln for ln in self.lines if not os.path.exists(ln["_raw"])}.values())
        unique = len({ln["_raw"] for ln in self.lines})
        emit("voces", hecho=unique - len(todo), total=unique,
             mensaje=f"{len(self.lines)} líneas ({unique} audios distintos); {unique - len(todo)} ya estaban generados")
        if todo:
            (self.tts_indextts if engine == "indextts" else self.tts_edge)(todo)
        skipped = {ln["_raw"] for ln in todo if ln.get("_skip")}
        self.lines = [ln for ln in self.lines if ln["_raw"] not in skipped]
        for ln in self.lines:
            ln["_audio"] = self.trimmed(ln["_raw"], os.path.join(self.work, "tts", ln["_key"] + "_t.wav"))

        if engine == "indextts":
            # Las emociones (tristeza, sorpresa) alargan mucho algunas frases: se regeneran más rápido.
            redo, factors = [], {}
            for i, ln in enumerate(self.lines):
                room = self.room(i, ln["s"])
                d = self.dur(ln["_audio"])
                if d / room > REGEN_RATIO:
                    factor = round(max(0.5, room * REGEN_RATIO / d), 2)
                    factors[ln["id"]] = factor
                    ln["_key"] = key(ln["_key"], "rapido", factor) + "_r"
                    ln["_raw"] = os.path.join(self.work, "tts", ln["_key"] + ".wav")
                    if not os.path.exists(ln["_raw"]):
                        redo.append(ln)
            redo = list({ln["_raw"]: ln for ln in redo}.values())
            if redo:
                emit("voces", hecho=0, total=len(redo), mensaje=f"Regenerando {len(redo)} líneas largas más rápido")
                self.tts_indextts(redo, factors)
            for ln in self.lines:
                if ln["_key"].endswith("_r"):
                    ln["_audio"] = self.trimmed(ln["_raw"], os.path.join(self.work, "tts", ln["_key"] + "_t.wav"))

    # ---------- 2) voces propias (Seed-VC) ----------
    def convert_voices(self):
        cfg = self.p.get("seedvc")
        if not cfg:
            return
        jobs, prepared = [], {}
        for ln in self.lines:
            sample = ln["_v"].get("sample")
            if not sample:
                continue
            if sample not in prepared:
                # Muestra limpia: sin ruidos graves ni silencios largos, volumen igualado y como mucho 24 s.
                dst = os.path.join(self.work, "vc", "muestra_" + ln["_v"]["sample_key"] + ".wav")
                if not os.path.exists(dst):
                    run(self.ffmpeg, "-y", "-v", "error", "-i", sample, "-af",
                        "highpass=f=70,silenceremove=start_periods=1:start_threshold=-45dB:stop_periods=-1:"
                        "stop_duration=0.4:stop_threshold=-45dB,loudnorm=I=-20:TP=-2,atrim=0:24",
                        "-ac", "1", "-ar", "44100", dst + ".tmp.wav")
                    os.replace(dst + ".tmp.wav", dst)
                prepared[sample] = dst
            sample = prepared[sample]
            out = os.path.join(self.work, "vc", key(ln["_key"], ln["_v"]["sample_key"], cfg.get("steps", 30)) + ".wav")
            ln["_vc"] = out
            if not os.path.exists(out) and all(j["output"] != out for j in jobs):
                jobs.append(dict(source=ln["_audio"], target=sample, output=out))
        if jobs:
            emit("voces_propias", hecho=0, total=len(jobs), mensaje=f"Aplicando voces propias a {len(jobs)} líneas")
            self.batch(cfg["python"], cfg["script"], cfg["repo"], jobs, "voces_propias",
                       extra_env={"VC_STEPS": str(cfg.get("steps", 30))})
        for ln in self.lines:
            if ln.get("_vc"):
                ln["_audio"] = self.trimmed(ln["_vc"], ln["_vc"][:-4] + "_t.wav")

    # ---------- 3) tiempos ----------
    def dur(self, path):
        with wave.open(path, "rb") as w:
            return w.getnframes() / w.getframerate()

    def room(self, i, start):
        nxt = self.lines[i + 1]["s"] if i + 1 < len(self.lines) else (self.t1 - self.t0)
        return max(nxt - start - GAP, self.lines[i]["e"] - start, 0.3)

    def fit(self):
        cursor, report = 0.0, []
        for i, ln in enumerate(self.lines):
            d = self.dur(ln["_audio"])
            start = max(ln["s"], cursor + GAP)
            room = self.room(i, start)
            tempo = min(max(d / room, 1.0), MAX_TEMPO)
            final = os.path.join(self.work, "final", key(ln["_audio"], round(tempo, 3)) + ".wav")
            if not os.path.exists(final):
                af = f"atempo={tempo:.3f}" if tempo > 1.0 else "anull"
                run(self.ffmpeg, "-y", "-v", "error", "-i", ln["_audio"], "-af", af + ",aformat=sample_fmts=s16",
                    "-ac", "1", "-ar", str(SR), final + ".tmp.wav")
                os.replace(final + ".tmp.wav", final)
            length = self.dur(final)
            ln["_final"], ln["_start"] = final, start
            cursor = start + length
            over = length - room
            report.append(dict(id=ln["id"], tempo=round(tempo, 2), retraso=round(start - ln["s"], 2),
                               desborde=round(over, 2) if over > 0.05 else 0.0))  # < 50 ms: redondeo de atempo
        return report

    def voice_track(self, path):
        """Pista de voces completa escrita de forma secuencial (las líneas nunca se pisan)."""
        total = int((self.t1 - self.t0) * SR)
        with wave.open(path, "wb") as out:
            out.setnchannels(1)
            out.setsampwidth(2)
            out.setframerate(SR)
            pos = 0
            silence = b"\0\0" * SR
            for ln in self.lines:
                start = int(ln["_start"] * SR)
                while pos < start:
                    n = min(start - pos, SR)
                    out.writeframes(silence[: n * 2])
                    pos += n
                with wave.open(ln["_final"], "rb") as w:
                    frames = w.readframes(w.getnframes())
                frames = frames[: max(0, (total - pos)) * 2]
                out.writeframes(frames)
                pos += len(frames) // 2
            while pos < total:
                n = min(total - pos, SR)
                out.writeframes(silence[: n * 2])
                pos += n

    # ---------- 4) mezcla y video final ----------
    def channel_count(self, idx):
        out = subprocess.run([self.ffprobe, "-v", "error", "-select_streams", f"a:{idx}", "-show_entries",
                              "stream=channels", "-of", "json", self.p["video"]],
                             capture_output=True, text=True, creationflags=NO_WINDOW).stdout
        streams = json.loads(out or "{}").get("streams") or [{}]
        return int(streams[0].get("channels") or 2)

    def background_filter(self):
        """Fondo a partir de la pista original. En 5.1/7.1 los diálogos van en el canal central (c2): se quita.
        Los canales se nombran por posición (c0…c7), que vale para cualquier variante (5.1, 5.1(side), 7.1, 7.1(wide)…):
        c0/c1 frontales, c2 central, c3 graves, c4/c5 traseros o laterales, c6/c7 los extra del 7.1."""
        idx = self.p.get("audio_index", 0)
        n = self.channel_count(idx)
        src = f"[0:a:{idx}]"
        if n >= 6:
            extra_l = "+0.4*c6" if n >= 8 else ""
            extra_r = "+0.4*c7" if n >= 8 else ""
            return (f"{src}pan=stereo|c0=0.8*c0+0.5*c4+0.3*c3{extra_l}|c1=0.8*c1+0.5*c5+0.3*c3{extra_r}", True)
        return (f"{src}aformat=channel_layouts=stereo", False)

    def fragment_srt(self):
        """Subtítulos del fragmento con los tiempos relativos al corte."""
        def ts(x):
            ms = round(max(x, 0) * 1000)
            h, ms = divmod(ms, 3600000)
            m, ms = divmod(ms, 60000)
            s, ms = divmod(ms, 1000)
            return f"{h:02}:{m:02}:{s:02},{ms:03}"
        path = os.path.join(self.work, "fragmento.srt")
        n = 0
        with open(path, "w", encoding="utf-8") as f:
            for ln in sorted(self.p["lines"], key=lambda x: x["start"]):
                if ln["end"] <= self.t0 or ln["start"] >= self.t1:
                    continue
                n += 1
                f.write(f"{n}\n{ts(ln['start'] - self.t0)} --> {ts(min(ln['end'], self.t1) - self.t0)}\n{ln['text']}\n\n")
        return path

    def mux(self, voice_wav, report):
        out = self.p["output"]
        tmp = out + ".tmp.mkv"
        rng = self.p.get("range")
        bg, center_removed = self.background_filter()
        # Sin canal central el fondo ya no tiene diálogos: se baja poco. En estéreo los diálogos originales siguen
        # ahí: se baja más mientras habla la voz nueva.
        duck = "threshold=0.03:ratio=4:attack=20:release=300" if center_removed else "threshold=0.02:ratio=10:attack=10:release=400"
        length = self.t1 - self.t0
        graph = ";".join([
            f"{bg},atrim=0:{length:.3f},asetpts=N/SR/TB[fondo]",
            f"[1:a]aformat=channel_layouts=stereo,volume=1.6,asplit=2[voz1][voz2]",
            f"[fondo][voz1]sidechaincompress={duck}[fondo_d]",
            "[fondo_d][voz2]amix=inputs=2:normalize=0:duration=first,alimiter=limit=0.9[doblaje]",
        ])
        args = [self.ffmpeg, "-y", "-v", "error", "-stats_period", "2", "-progress", "pipe:1"]
        if rng:
            args += ["-ss", f"{self.t0:.3f}", "-t", f"{length:.3f}"]
        args += ["-i", self.p["video"], "-i", voice_wav]
        subs = self.p.get("subtitles")
        if subs and rng:
            subs = self.fragment_srt()
        if subs:
            args += ["-i", subs]
        args += ["-filter_complex", graph, "-map", "0:v:0", "-map", "[doblaje]", "-map", "0:a?"]
        if subs:
            args += ["-map", "2:s"]
        if not rng:
            args += ["-map", "0:s?", "-map", "0:t?"]  # subtítulos y adjuntos (fuentes de los ASS) originales
        if rng:  # fragmento de prueba: se recodifica para que el corte sea exacto y el archivo pequeño
            args += ["-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-vf", "scale=-2:'min(720,ih)'"]
        else:
            args += ["-c:v", "copy"]
        title = self.p.get("track_title", "Español (cubano, doblaje IA)")
        # Los subtítulos de un .mp4 (mov_text) no caben tal cual en un .mkv: se pasan a SRT.
        sub_codec = "srt" if rng or self.p["video"].lower().endswith((".mp4", ".m4v")) else "copy"
        args += ["-c:a", "copy", "-c:a:0", "aac", "-b:a:0", "192k", "-c:s", sub_codec, "-c:t", "copy",
                 "-metadata:s:a:0", "language=spa", "-metadata:s:a:0", f"title={title}",
                 "-disposition:a", "0", "-disposition:a:0", "default"]
        if subs:
            args += ["-c:s:0", "srt", "-metadata:s:s:0", "language=spa", "-metadata:s:s:0", f"title={self.p.get('subtitle_title', 'Cubano')}",
                     "-disposition:s", "0", "-disposition:s:0", "default"]
        args += ["-max_interleave_delta", "0", tmp]
        emit("mezcla", hecho=0, total=100, mensaje="Mezclando y creando el video")
        proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=NO_WINDOW,
                                text=True, encoding="utf-8", errors="replace")
        err = []
        import threading
        t = threading.Thread(target=lambda: err.extend(proc.stderr), daemon=True)
        t.start()
        for line in proc.stdout:
            if line.startswith("out_time_us="):
                try:
                    secs = int(line.split("=")[1]) / 1e6
                    emit("mezcla", hecho=int(min(99, secs / max(length, 1) * 100)), total=100, mensaje="Mezclando y creando el video")
                except ValueError:
                    pass
        proc.wait()
        t.join(timeout=5)
        if proc.returncode != 0:
            tail = [l.strip() for l in err if l.strip()]
            raise RuntimeError("ffmpeg falló al crear el video: " + (tail[-1] if tail else f"código {proc.returncode}"))
        os.replace(tmp, out)
        return out

    def run(self):
        if not self.lines:
            raise RuntimeError("No hay líneas con texto en ese tramo.")
        self.generate_voices()
        self.convert_voices()
        emit("tiempos", hecho=0, total=1, mensaje="Ajustando tiempos")
        report = self.fit()
        voice_wav = os.path.join(self.work, "voces.wav")
        self.voice_track(voice_wav)
        out = self.mux(voice_wav, report)
        resumen = dict(lineas=len(report), aceleradas=sum(r["tempo"] > 1 for r in report),
                       desbordan=sum(r["desborde"] > 0 for r in report),
                       retraso_max=max((r["retraso"] for r in report), default=0))
        with open(os.path.join(self.work, "informe.json"), "w", encoding="utf-8") as f:
            json.dump(dict(resumen=resumen, lineas=report), f, ensure_ascii=False, indent=1)
        emit("fin", salida=out, informe=resumen)


def main():
    if len(sys.argv) != 2:
        print("Uso: motor_doblaje.py plan.json", file=sys.stderr)
        return 2
    with open(sys.argv[1], encoding="utf-8") as f:
        plan = json.load(f)
    try:
        Motor(plan).run()
        return 0
    except Exception as ex:  # el detalle va a la app
        emit("error", mensaje=str(ex))
        return 1


if __name__ == "__main__":
    sys.exit(main())
