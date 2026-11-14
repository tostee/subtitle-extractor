"""IndexTTS-2.5 por lotes para Subtítulos: carga el modelo una vez y genera muchas líneas con emoción.

Uso (con el Python del entorno de IndexTTS): python lote_indextts.py <carpeta_del_repo> trabajos.json
trabajos.json = [{"text": "...", "speaker": "ref.wav", "emo": [8 floats], "output": "salida.wav",
                  "duration_factor": 1.0}, ...]
Orden de "emo": [alegría, ira, tristeza, miedo, asco, melancolía, sorpresa, calma]
Progreso por stdout: {"hecho": n, "total": t, "mensaje": "..."} (una línea JSON por línea generada).
"""
import json
import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # la app lee UTF-8 (con -I se ignora PYTHONIOENCODING)

repo = os.path.abspath(sys.argv[1])
jobs_path = os.path.abspath(sys.argv[2])
os.chdir(repo)
sys.path.insert(0, repo)

from indextts.infer_v2_5 import IndexTTS2  # noqa: E402

jobs = json.load(open(jobs_path, encoding="utf-8"))
alpha = float(os.environ.get("EMO_ALPHA", "1.0"))
tts = IndexTTS2(cfg_path="checkpoints/config.yaml", model_dir="checkpoints", use_bf16=True)
t0 = time.time()
for k, job in enumerate(jobs, 1):
    emo = job.get("emo")
    kwargs = dict(emo_vector=emo, use_random=False, emo_alpha=alpha) if emo and any(emo) else {}
    tmp = job["output"] + ".tmp.wav"
    tts.infer(spk_audio_prompt=job["speaker"], text=job["text"], lang="ES", output_path=tmp, verbose=False,
              duration_factor=float(job.get("duration_factor", 1.0)), **kwargs)
    os.replace(tmp, job["output"])
    print(json.dumps({"hecho": k, "total": len(jobs), "mensaje": f"Voz con emoción {k} de {len(jobs)}"}), flush=True)
print(json.dumps({"hecho": len(jobs), "total": len(jobs), "mensaje": f"IndexTTS-2.5: {len(jobs)} líneas en {time.time() - t0:.0f} s"}), flush=True)
