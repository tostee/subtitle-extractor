"""Seed-VC por lotes para Subtítulos: carga el modelo una vez y cambia el timbre de muchos clips.

Uso (con el Python del entorno de Seed-VC): python lote_seedvc.py <carpeta_del_repo> trabajos.json
trabajos.json = [{"source": "a.wav", "target": "muestra.wav", "output": "salida.wav"}, ...]
Progreso por stdout: {"hecho": n, "total": t, "mensaje": "..."} (una línea JSON por clip).
"""
import argparse
import json
import os
import shutil
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # la app lee UTF-8 (con -I se ignora PYTHONIOENCODING)

repo = os.path.abspath(sys.argv[1])
jobs_path = os.path.abspath(sys.argv[2])
os.chdir(repo)  # el repo usa rutas relativas (checkpoints/, configs/)
sys.path.insert(0, repo)

import inference  # noqa: E402

_cache = {}
_orig_load = inference.load_models


def _cached_load(args):
    if "m" not in _cache:
        _cache["m"] = _orig_load(args)
    return _cache["m"]


inference.load_models = _cached_load

jobs = json.load(open(jobs_path, encoding="utf-8"))
steps = int(os.environ.get("VC_STEPS", "30"))
t0 = time.time()
for k, job in enumerate(jobs, 1):
    tmp_dir = os.path.join(os.path.dirname(job["output"]), "_vc_tmp")
    args = argparse.Namespace(source=job["source"], target=job["target"], output=tmp_dir,
                              diffusion_steps=steps, length_adjust=1.0, inference_cfg_rate=0.7,
                              f0_condition=False, auto_f0_adjust=False, semi_tone_shift=0,
                              checkpoint=None, config=None, fp16=True)
    inference.main(args)
    src = os.path.basename(job["source"]).split(".")[0]
    tgt = os.path.basename(job["target"]).split(".")[0]
    produced = os.path.join(tmp_dir, f"vc_{src}_{tgt}_1.0_{steps}_0.7.wav")
    os.replace(produced, job["output"])
    print(json.dumps({"hecho": k, "total": len(jobs), "mensaje": f"Voz propia {k} de {len(jobs)}"}), flush=True)
shutil.rmtree(os.path.join(os.path.dirname(jobs[0]["output"]), "_vc_tmp"), ignore_errors=True) if jobs else None
print(json.dumps({"hecho": len(jobs), "total": len(jobs), "mensaje": f"Seed-VC: {len(jobs)} clips en {time.time() - t0:.0f} s"}), flush=True)
