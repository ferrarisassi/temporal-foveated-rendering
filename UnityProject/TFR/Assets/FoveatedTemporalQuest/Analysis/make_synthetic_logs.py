#!/usr/bin/env python3
"""
Gera logs SINTÉTICOS no mesmo formato do SessionLogger, só para testar o analyze.py
antes de ter dados reais do Quest. Os números NÃO são resultados: o custo de GPU é
inventado a partir de um modelo simples (custo fixo + custo de fragmentos × fração renderizada).

Uso: python make_synthetic_logs.py <pasta_saida> [--participants 6]
"""
import argparse
import math
import os

import numpy as np

HZ = 90.0
TASKS = [("parado", 0), ("olhar_devagar", 20), ("girar_moderado", 70), ("girar_rapido", 160), ("andar", -1), ("livre", -2)]
CONDS = ["baseline", "overhead", "fixo", "adaptativo"]
AREA = (0.22, 0.38, 0.40)  # fóvea, média, distante (fração da imagem)

HEADER = ("time_s,frame,participant,block,condition,task,phase,activity,ang_dps,ang_raw_dps,lin_mps,hand_mps,speed_norm,"
          "display_hz,div_mid,div_far,target_hz_mid,target_hz_far,fresh_mid,fresh_far,eff_hz_mid,eff_hz_far,"
          "area_fovea,area_mid,area_far,skip_frac_pred,ftm_cpu_ms,ftm_gpu_ms,xr_app_gpu_raw,xr_dropped_total,"
          "meta_app_gpu_ms,meta_app_cpu_ms,meta_gpu_util,meta_cpu_util,meta_gpu_mhz,meta_cpu_mhz,"
          "battery_temp_c,battery_current_ma,battery_voltage_v,power_w,backbuffer_warning,events")


def latin_row(n, p):
    first = [0]
    a, b = 1, n - 1
    for i in range(1, n):
        if i % 2 == 1:
            first.append(a); a += 1
        else:
            first.append(b); b -= 1
    return [(x + p) % n for x in first]


def activity(ang, lin):
    if ang >= 120 or lin >= 1.2: return "movimento_rapido"
    if lin >= 0.35: return "andando"
    if ang < 4 and lin < 0.05: return "parado"
    if ang < 35: return "olhando_devagar"
    return "girando_cabeca"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--participants", type=int, default=6)
    ap.add_argument("--seconds", type=float, default=12.0, help="duração da medição por tarefa")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    rng = np.random.default_rng(42)

    for p in range(args.participants):
        pid = f"P{p + 1:02d}"
        frag_cost = rng.normal(7.0, 0.4)   # ms de fragmentos se tudo for renderizado
        fixed_cost = rng.normal(2.0, 0.2)  # ms fixos (vértices, composição...)
        fpath = os.path.join(args.out, f"frames_{pid}_synthetic.csv")
        epath = os.path.join(args.out, f"events_{pid}_synthetic.csv")
        t, frame, block, dropped = 0.0, 0, 0, 0
        with open(fpath, "w") as ff, open(epath, "w") as fe:
            ff.write(HEADER + "\n")
            fe.write("time_s,frame,participant,block,condition,task,phase,type,detail\n")
            for rep in range(2):
                for ci in latin_row(4, p):
                    cond = CONDS[ci]
                    block += 1
                    for task, peak in TASKS:
                        since = [0, 0]
                        n = int(args.seconds * HZ)
                        upd = [[], []]
                        for i in range(n):
                            tt = i / HZ
                            if peak >= 0:
                                ang = abs(peak * math.cos(2 * math.pi * (peak / (2 * math.pi * 60 if peak else 1)) * tt)) if peak else 0.0
                                lin = 0.01
                            elif peak == -1:
                                ang, lin = abs(rng.normal(12, 5)), abs(0.6 + 0.3 * math.sin(tt))
                            else:
                                ang, lin = abs(rng.normal(45, 35)), abs(rng.normal(0.1, 0.1))
                            ang = max(0.0, ang + rng.normal(0, 1.0))
                            s = min(1.0, max(ang / 120, lin / 1.2))
                            if cond in ("baseline", "overhead"):
                                div = [1, 1]
                            elif cond == "fixo":
                                div = [2, 2]
                            else:
                                hz_mid = 18 + (HZ - 18) * s
                                hz_far = 6 + (HZ - 6) * (0.7 * s if s < 0.5 else 0.35 + 1.3 * (s - 0.5))
                                div = [max(1, min(30, round(HZ / hz_mid))), max(1, min(30, round(HZ / hz_far)))]
                                div[1] = max(div[1], div[0])
                            stale = [False, False]
                            for k in range(2):
                                if div[k] > 1:
                                    since[k] += 1
                                    if since[k] >= div[k]:
                                        since[k] = 0
                                    else:
                                        stale[k] = True
                            skip = (AREA[1] if stale[0] else 0) + (AREA[2] if stale[1] else 0)
                            active = cond == "overhead" or div[0] > 1 or div[1] > 1
                            overhead = 0.0 if not active else 0.35 + (0.6 if cond == "overhead" else 0.0)
                            gpu = fixed_cost + frag_cost * (1 - 0.85 * skip) + overhead + rng.normal(0, 0.3)
                            if gpu > 1000 / HZ and rng.random() < 0.3:
                                dropped += 1
                            now = t + tt
                            for k in range(2):
                                if not stale[k]:
                                    upd[k].append(now)
                                upd[k] = [x for x in upd[k] if now - x <= 1.0]
                            ev = ""
                            if (cond == "adaptativo" and stale[1] and ang > 60 and rng.random() < 0.002) or rng.random() < 0.0004:
                                ev = "artefato_reportado"
                                fe.write(f"{now:.4f},{frame},{pid},{block},{cond},{task},medicao,artefato_reportado,\n")
                            power = 4.2 + 0.25 * gpu + rng.normal(0, 0.2)
                            row = [f"{now:.4f}", frame, pid, block, cond, task, "medicao", activity(ang, lin),
                                   f"{ang:.2f}", f"{ang:.2f}", f"{lin:.3f}", "0", f"{s:.3f}", f"{HZ:.1f}", div[0], div[1],
                                   f"{HZ / div[0]:.2f}", f"{HZ / div[1]:.2f}", int(not stale[0]), int(not stale[1]),
                                   len(upd[0]), len(upd[1]), *AREA, f"{skip:.4f}",
                                   f"{rng.normal(6, 0.5):.3f}", f"{gpu:.3f}", f"{gpu / 1000:.6f}", dropped,
                                   f"{gpu:.3f}", f"{rng.normal(6, 0.5):.3f}", f"{min(100, gpu / (1000 / HZ) * 100):.1f}", "55",
                                   "490", "1478", "32.0", f"{power / 3.85 * 1000:.0f}", "3.850", f"{power:.3f}", 0, ev]
                            ff.write(",".join(str(x) for x in row) + "\n")
                            frame += 1
                        t += n / HZ + 9.0
                    answer = "sim" if (cond == "adaptativo" and rng.random() < 0.4) or (cond == "fixo" and rng.random() < 0.25) or rng.random() < 0.08 else "nao"
                    fe.write(f"{t:.4f},{frame},{pid},{block},{cond},-,pergunta,resposta_percebeu,{answer}\n")
                    if answer == "sim":
                        fe.write(f"{t + 1:.4f},{frame},{pid},{block},{cond},-,pergunta,resposta_incomodo,{'bastante' if rng.random() < 0.3 else 'pouco'}\n")
        print("gerado", fpath)


if __name__ == "__main__":
    main()
