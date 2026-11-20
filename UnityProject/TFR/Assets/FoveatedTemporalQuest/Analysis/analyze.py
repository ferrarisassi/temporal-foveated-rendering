#!/usr/bin/env python3
"""
Análise dos logs do projeto Foveated Temporal Rendering (Quest 2).

Uso:
    python analyze.py <pasta_com_logs> [--out <pasta_saida>]

A pasta deve conter os arquivos frames_*.csv e events_*.csv gerados pelo SessionLogger
(adb pull /sdcard/Android/data/<pacote>/files/FTR_Logs).

Saídas (em --out, padrão <pasta>/resultados):
    resumo_condicao_tarefa.csv   métricas por condição × tarefa
    economia_por_tarefa.csv      economia de GPU vs baseline (por participante e média)
    economia_por_atividade.csv   economia vs baseline por atividade detectada automaticamente
    curva_velocidade.csv         GPU e frame rate efetivo da periferia por faixa de velocidade
    percepcao.csv                relatos de artefato e respostas por condição
    relatorio.md                 resumo legível com testes estatísticos
    *.png                        gráficos
"""
import argparse
import glob
import os
import sys

import numpy as np
import pandas as pd

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

try:
    from scipy import stats
except ImportError:  # scipy é opcional
    stats = None

CONDITIONS = ["baseline", "overhead", "fixo", "adaptativo"]
COND_LABEL = {"baseline": "Baseline", "overhead": "Só overhead", "fixo": "Fixo", "adaptativo": "Adaptativo"}
# Paleta categórica validada (ordem fixa por condição)
COND_COLOR = {"baseline": "#2a78d6", "overhead": "#eb6834", "fixo": "#1baf7a", "adaptativo": "#eda100"}
ACTIVITIES = ["parado", "olhando_devagar", "girando_cabeca", "andando", "movimento_rapido"]
TEXT = "#2b2b29"
MUTED = "#6b6a63"
GRID = "#e4e3de"


# --------------------------------------------------------------------------- carga
def load(folder):
    frames = sorted(glob.glob(os.path.join(folder, "frames_*.csv")))
    events = sorted(glob.glob(os.path.join(folder, "events_*.csv")))
    if not frames:
        sys.exit(f"Nenhum frames_*.csv em {folder}")
    fr = []
    for i, f in enumerate(frames):
        d = pd.read_csv(f, keep_default_na=True)
        d["session"] = os.path.basename(f)
        fr.append(d)
    ev = []
    for f in events:
        d = pd.read_csv(f)
        d["session"] = os.path.basename(f).replace("events_", "frames_")
        ev.append(d)
    fr = pd.concat(fr, ignore_index=True)
    ev = pd.concat(ev, ignore_index=True) if ev else pd.DataFrame(columns=["type", "condition", "phase"])
    return fr, ev


def pick_gpu_column(df):
    """Escolhe a melhor fonte de tempo de GPU disponível e devolve (série em ms, nome da fonte)."""
    candidates = []
    if "meta_app_gpu_ms" in df:
        candidates.append(("meta_app_gpu_ms", df["meta_app_gpu_ms"]))
    if "xr_app_gpu_raw" in df:
        raw = df["xr_app_gpu_raw"]
        med = raw[raw > 0].median()
        if pd.notna(med):
            candidates.append(("xr_app_gpu (s→ms)" if med < 1.0 else "xr_app_gpu (ms)",
                               raw * 1000.0 if med < 1.0 else raw))
    if "ftm_gpu_ms" in df:
        candidates.append(("ftm_gpu_ms", df["ftm_gpu_ms"]))
    for name, s in candidates:
        valid = s[(s > 0) & s.notna()]
        if len(valid) > 0.5 * len(s):
            return s.where(s > 0), name
    sys.exit("Nenhuma coluna de GPU com dados válidos (meta_app_gpu_ms, xr_app_gpu_raw, ftm_gpu_ms).")


# --------------------------------------------------------------------------- métricas
def p95(x):
    return np.nanpercentile(x, 95) if len(x) else np.nan


def summarize(m):
    g = m.groupby(["condition", "task"])
    out = g.agg(
        quadros=("gpu_ms", "size"),
        gpu_med_ms=("gpu_ms", "median"),
        gpu_media_ms=("gpu_ms", "mean"),
        gpu_p95_ms=("gpu_ms", p95),
        cpu_med_ms=("ftm_cpu_ms", "median"),
        gpu_util_pct=("meta_gpu_util", "mean"),
        potencia_w=("power_w", "mean"),
        vel_ang_media_dps=("ang_dps", "mean"),
        hz_efetivo_media=("eff_hz_mid", "mean"),
        hz_efetivo_distante=("eff_hz_far", "mean"),
        fracao_pulada_prevista=("skip_frac_pred", "mean"),
    ).reset_index()
    # quadros perdidos por minuto (contador acumulado do runtime XR)
    drops = []
    for (c, t), d in m.groupby(["condition", "task"]):
        total, dur = 0.0, 0.0
        for _, seg in d.groupby(["session", "block"]):
            x = seg["xr_dropped_total"].dropna()
            if len(x) > 1:
                total += max(0.0, x.iloc[-1] - x.iloc[0])
            dur += seg["time_s"].max() - seg["time_s"].min()
        drops.append((c, t, total / dur * 60.0 if dur > 0 else np.nan))
    drops = pd.DataFrame(drops, columns=["condition", "task", "quadros_perdidos_min"])
    return out.merge(drops, on=["condition", "task"], how="left")


def savings_by(m, key):
    """Economia de GPU vs baseline: MÉDIA por participante×key×condição, depois média entre participantes.

    Usa a média (não a mediana) porque o custo por quadro é bimodal na foveação temporal:
    quadros de atualização custam como o baseline e quadros "stale" custam menos. A média é o que
    determina consumo, temperatura e folga média de GPU; o pior caso aparece no p95 do resumo.
    """
    med = m.groupby(["participant", key, "condition"])["gpu_ms"].mean().unstack("condition")
    if "baseline" not in med:
        return pd.DataFrame(), pd.DataFrame()
    rows = []
    for cond in [c for c in CONDITIONS if c != "baseline" and c in med]:
        s = (1.0 - med[cond] / med["baseline"]) * 100.0
        for (p, k), v in s.dropna().items():
            rows.append({"participant": p, key: k, "condition": cond, "economia_pct": v,
                         "gpu_baseline_ms": med.loc[(p, k), "baseline"], "gpu_cond_ms": med.loc[(p, k), cond]})
    per = pd.DataFrame(rows)
    if per.empty:
        return per, per
    agg = per.groupby([key, "condition"]).agg(
        participantes=("economia_pct", "size"),
        economia_media_pct=("economia_pct", "mean"),
        economia_dp_pct=("economia_pct", "std"),
        gpu_baseline_ms=("gpu_baseline_ms", "mean"),
        gpu_cond_ms=("gpu_cond_ms", "mean"),
    ).reset_index()
    # teste pareado (Wilcoxon) baseline vs condição, entre participantes
    pvals = []
    for (k, cond), d in per.groupby([key, "condition"]):
        p = np.nan
        if stats is not None and len(d) >= 5:
            try:
                p = stats.wilcoxon(d["gpu_baseline_ms"], d["gpu_cond_ms"]).pvalue
            except ValueError:
                pass
        pvals.append((k, cond, p))
    agg = agg.merge(pd.DataFrame(pvals, columns=[key, "condition", "p_wilcoxon"]), on=[key, "condition"])
    return per, agg


def speed_curve(m):
    bins = np.linspace(0, 1, 11)
    m = m.copy()
    m["faixa_vel"] = pd.cut(m["speed_norm"].clip(0, 1), bins, include_lowest=True)
    c = m.groupby(["condition", "faixa_vel"], observed=True).agg(
        quadros=("gpu_ms", "size"),
        gpu_media_ms=("gpu_ms", "mean"),
        hz_medio=("eff_hz_mid", "mean"),
        hz_distante=("eff_hz_far", "mean"),
        vel_ang_media_dps=("ang_dps", "mean"),
    ).reset_index()
    c["vel_centro"] = c["faixa_vel"].apply(lambda iv: iv.mid).astype(float)
    return c


def perception(m, ev):
    rows = []
    meas_min = m.groupby("condition").apply(
        lambda d: sum(seg["time_s"].max() - seg["time_s"].min()
                      for _, seg in d.groupby(["session", "block", "task"])) / 60.0,
        include_groups=False)
    for cond in CONDITIONS:
        e = ev[ev["condition"] == cond] if "condition" in ev else ev.iloc[0:0]
        rep = e[(e["type"] == "artefato_reportado") & (e["phase"] == "medicao")]
        ans = e[e["type"] == "resposta_percebeu"]
        inc = e[e["type"] == "resposta_incomodo"]
        mins = meas_min.get(cond, np.nan)
        rows.append({
            "condition": cond,
            "minutos_medidos": mins,
            "relatos_artefato": len(rep),
            "relatos_por_min": len(rep) / mins if mins and mins > 0 else np.nan,
            "respostas": len(ans),
            "percebeu_pct": (ans["detail"] == "sim").mean() * 100 if len(ans) else np.nan,
            "incomodou_bastante_pct": (inc["detail"] == "bastante").mean() * 100 if len(inc) else np.nan,
        })
    return pd.DataFrame(rows)


# --------------------------------------------------------------------------- gráficos
def style(ax, title, ylabel):
    ax.set_title(title, loc="left", fontsize=12, color=TEXT, pad=10)
    ax.set_ylabel(ylabel, color=MUTED)
    ax.tick_params(colors=MUTED, length=0)
    for s in ("top", "right", "left"):
        ax.spines[s].set_visible(False)
    ax.spines["bottom"].set_color(GRID)
    ax.yaxis.grid(True, color=GRID, linewidth=0.8)
    ax.set_axisbelow(True)


def grouped_bars(ax, df, cat_col, cats, val_col, conds, fmt="{:.1f}", err_col=None):
    conds = [c for c in conds if c in set(df["condition"])]
    n = max(1, len(conds))
    width = 0.8 / n
    x = np.arange(len(cats))
    for i, cond in enumerate(conds):
        d = df[df["condition"] == cond].set_index(cat_col)
        vals = [d[val_col].get(c, np.nan) for c in cats]
        errs = [d[err_col].get(c, np.nan) for c in cats] if err_col else None
        pos = x - 0.4 + width * (i + 0.5)
        bars = ax.bar(pos, vals, width * 0.92, color=COND_COLOR[cond], label=COND_LABEL[cond],
                      yerr=errs, error_kw={"ecolor": MUTED, "elinewidth": 1, "capsize": 2})
        for b, v in zip(bars, vals):
            if pd.notna(v):
                ax.annotate(fmt.format(v), (b.get_x() + b.get_width() / 2, max(v, 0)),
                            xytext=(0, 2), textcoords="offset points", ha="center", va="bottom",
                            fontsize=7, color=TEXT)
    ax.set_xticks(x)
    ax.set_xticklabels([c.replace("_", " ") for c in cats], color=TEXT)
    ax.legend(frameon=False, fontsize=8, ncol=n, loc="upper left", bbox_to_anchor=(0, -0.12))


def charts(summary, sav_task, sav_act, curve, perc, out):
    tasks = list(dict.fromkeys(summary["task"]))

    fig, ax = plt.subplots(figsize=(10, 4.8))
    grouped_bars(ax, summary, "task", tasks, "gpu_media_ms", CONDITIONS)
    style(ax, "Tempo de GPU por quadro (média) por tarefa e condição", "ms por quadro")
    fig.tight_layout(); fig.savefig(os.path.join(out, "gpu_por_tarefa.png"), dpi=160); plt.close(fig)

    if not sav_act.empty:
        acts = [a for a in ACTIVITIES if a in set(sav_act["atividade"])]
        fig, ax = plt.subplots(figsize=(10, 4.8))
        grouped_bars(ax, sav_act, "atividade", acts, "economia_media_pct",
                     [c for c in CONDITIONS if c != "baseline"], fmt="{:.0f}%", err_col="economia_dp_pct")
        ax.axhline(0, color=MUTED, linewidth=1)
        style(ax, "Economia de GPU vs baseline, pela atividade do usuário", "% de economia (média ± dp)")
        fig.tight_layout(); fig.savefig(os.path.join(out, "economia_por_atividade.png"), dpi=160); plt.close(fig)

    # curva velocidade -> GPU e frame rate efetivo (dois gráficos, um eixo cada)
    fig, axes = plt.subplots(1, 2, figsize=(11, 4.2))
    for cond in CONDITIONS:
        d = curve[curve["condition"] == cond].sort_values("vel_centro")
        if d.empty:
            continue
        axes[0].plot(d["vel_centro"], d["gpu_media_ms"], color=COND_COLOR[cond], linewidth=2,
                     marker="o", markersize=4, label=COND_LABEL[cond])
    style(axes[0], "GPU por quadro × velocidade do usuário", "ms por quadro (média)")
    axes[0].set_xlabel("velocidade normalizada (0 = parado, 1 = rápido)", color=MUTED)
    axes[0].legend(frameon=False, fontsize=8)
    d = curve[curve["condition"] == "adaptativo"].sort_values("vel_centro")
    if not d.empty:
        axes[1].plot(d["vel_centro"], d["hz_medio"], color="#2a78d6", linewidth=2, marker="o", markersize=4,
                     label="Periferia média")
        axes[1].plot(d["vel_centro"], d["hz_distante"], color="#eb6834", linewidth=2, marker="o", markersize=4,
                     label="Periferia distante")
        axes[1].legend(frameon=False, fontsize=8)
    style(axes[1], "Adaptativo: frame rate efetivo da periferia", "atualizações por segundo")
    axes[1].set_xlabel("velocidade normalizada (0 = parado, 1 = rápido)", color=MUTED)
    fig.tight_layout(); fig.savefig(os.path.join(out, "curva_velocidade.png"), dpi=160); plt.close(fig)

    p = perc.dropna(subset=["relatos_por_min"])
    if not p.empty:
        fig, ax = plt.subplots(figsize=(6.5, 4))
        bars = ax.bar([COND_LABEL[c] for c in p["condition"]], p["relatos_por_min"],
                      color=[COND_COLOR[c] for c in p["condition"]], width=0.6)
        for b, v in zip(bars, p["relatos_por_min"]):
            ax.annotate(f"{v:.2f}", (b.get_x() + b.get_width() / 2, v), xytext=(0, 2),
                        textcoords="offset points", ha="center", fontsize=8, color=TEXT)
        style(ax, "Relatos de artefato durante a medição", "relatos por minuto")
        fig.tight_layout(); fig.savefig(os.path.join(out, "percepcao.png"), dpi=160); plt.close(fig)


# --------------------------------------------------------------------------- relatório
def md_table(df, cols, fmt=None):
    fmt = fmt or {}
    lines = ["| " + " | ".join(cols) + " |", "|" + "---|" * len(cols)]
    for _, r in df.iterrows():
        cells = []
        for c in cols:
            v = r[c]
            if isinstance(v, (float, np.floating)):
                cells.append("" if pd.isna(v) else fmt.get(c, "{:.2f}").format(v))
            else:
                cells.append(str(v))
        lines.append("| " + " | ".join(cells) + " |")
    return "\n".join(lines)


def report(m, gpu_src, summary, sav_task_agg, sav_act_agg, efficiency, perc, out):
    parts = ["# Resultados — Foveated Temporal Rendering", ""]
    parts.append(f"- Participantes: {m['participant'].nunique()} · sessões: {m['session'].nunique()} · "
                 f"quadros medidos: {len(m):,}".replace(",", "."))
    parts.append(f"- Fonte do tempo de GPU: `{gpu_src}`")
    parts.append(f"- Taxa do display: {m['display_hz'].median():.0f} Hz")
    parts.append("- Só a fase `medicao` entra na análise (instrução e aquecimento são descartados).")
    parts.append("")

    ov = sav_task_agg[sav_task_agg["condition"] == "overhead"] if not sav_task_agg.empty else sav_task_agg
    if not ov.empty:
        parts.append(f"**Custo fixo da técnica (overhead):** {(-ov['economia_media_pct']).mean():.1f}% de GPU a mais "
                     "que o baseline, em média, sem nenhuma economia. A economia das outras condições precisa superar isso.")
        parts.append("")

    parts.append("## Economia de GPU vs baseline, por tarefa")
    if not sav_task_agg.empty:
        parts.append(md_table(sav_task_agg, ["task", "condition", "participantes", "gpu_baseline_ms", "gpu_cond_ms",
                                             "economia_media_pct", "economia_dp_pct", "p_wilcoxon"],
                              {"p_wilcoxon": "{:.4f}", "economia_media_pct": "{:.1f}", "economia_dp_pct": "{:.1f}"}))
        if stats is None or sav_task_agg["p_wilcoxon"].isna().all():
            parts.append("\n_Teste de Wilcoxon exige scipy e pelo menos 5 participantes._")
    parts.append("\n![](gpu_por_tarefa.png)\n")

    parts.append("## Economia por atividade detectada (o que o usuário estava fazendo)")
    if not sav_act_agg.empty:
        parts.append(md_table(sav_act_agg, ["atividade", "condition", "participantes", "gpu_baseline_ms",
                                            "gpu_cond_ms", "economia_media_pct", "economia_dp_pct"],
                              {"economia_media_pct": "{:.1f}", "economia_dp_pct": "{:.1f}"}))
    parts.append("\n![](economia_por_atividade.png)\n")

    parts.append("## Velocidade × custo × frame rate da periferia")
    parts.append("![](curva_velocidade.png)\n")

    parts.append("## Economia prevista × medida")
    parts.append("A fração prevista é a área das zonas puladas × fração de quadros em que foram puladas. "
                 "Eficiência = economia medida / fração prevista. Perto de 1 significa que a cena é limitada por "
                 "fragment shading; bem abaixo de 1 significa que o custo de vértices, banda ou cópias domina.")
    if not efficiency.empty:
        parts.append(md_table(efficiency, ["task", "condition", "fracao_prevista_pct", "economia_medida_pct", "eficiencia"],
                              {"fracao_prevista_pct": "{:.1f}", "economia_medida_pct": "{:.1f}", "eficiencia": "{:.2f}"}))
    parts.append("")

    parts.append("## Percepção")
    parts.append(md_table(perc, ["condition", "minutos_medidos", "relatos_artefato", "relatos_por_min",
                                 "respostas", "percebeu_pct", "incomodou_bastante_pct"],
                          {"minutos_medidos": "{:.1f}", "percebeu_pct": "{:.0f}", "incomodou_bastante_pct": "{:.0f}"}))
    parts.append("\n![](percepcao.png)\n")

    parts.append("## Resumo completo por condição × tarefa")
    parts.append(md_table(summary, ["condition", "task", "quadros", "gpu_media_ms", "gpu_med_ms", "gpu_p95_ms", "gpu_util_pct",
                                    "potencia_w", "quadros_perdidos_min", "hz_efetivo_media", "hz_efetivo_distante"],
                          {"quadros_perdidos_min": "{:.1f}", "hz_efetivo_media": "{:.0f}", "hz_efetivo_distante": "{:.0f}"}))

    with open(os.path.join(out, "relatorio.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(parts) + "\n")


# --------------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folder")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    out = args.out or os.path.join(args.folder, "resultados")
    os.makedirs(out, exist_ok=True)

    fr, ev = load(args.folder)
    fr["gpu_ms"], gpu_src = pick_gpu_column(fr)
    m = fr[(fr["phase"] == "medicao") & fr["condition"].isin(CONDITIONS)].copy()
    if m.empty:
        sys.exit("Nenhum quadro na fase 'medicao'.")
    m = m.rename(columns={"activity": "atividade"})
    m["atividade"] = m["atividade"].fillna("desconhecida")
    m["backbuffer_warning"] = m.get("backbuffer_warning", 0)
    if (m["backbuffer_warning"] == 1).mean() > 0.01:
        print("AVISO: em mais de 1% dos quadros o efeito ficou inativo (backbuffer). Veja o README.")

    summary = summarize(m)
    sav_task_per, sav_task = savings_by(m, "task")
    sav_act_per, sav_act = savings_by(m, "atividade")
    curve = speed_curve(m)
    perc = perception(m, ev)

    # eficiência: economia medida vs fração de pixels pulada prevista
    eff = pd.DataFrame()
    if not sav_task.empty:
        pred = m.groupby(["task", "condition"])["skip_frac_pred"].mean().reset_index()
        eff = sav_task.merge(pred, on=["task", "condition"])
        eff = eff[eff["condition"].isin(["fixo", "adaptativo"])].copy()
        eff["fracao_prevista_pct"] = eff["skip_frac_pred"] * 100
        eff["economia_medida_pct"] = eff["economia_media_pct"]
        eff["eficiencia"] = eff["economia_medida_pct"] / eff["fracao_prevista_pct"].replace(0, np.nan)

    summary.to_csv(os.path.join(out, "resumo_condicao_tarefa.csv"), index=False)
    sav_task_per.to_csv(os.path.join(out, "economia_por_tarefa_participante.csv"), index=False)
    sav_task.to_csv(os.path.join(out, "economia_por_tarefa.csv"), index=False)
    sav_act.to_csv(os.path.join(out, "economia_por_atividade.csv"), index=False)
    curve.drop(columns=["faixa_vel"]).to_csv(os.path.join(out, "curva_velocidade.csv"), index=False)
    perc.to_csv(os.path.join(out, "percepcao.csv"), index=False)

    charts(summary, sav_task, sav_act, curve, perc, out)
    report(m, gpu_src, summary, sav_task, sav_act, eff, perc, out)
    print(f"Pronto. Resultados em {out}")


if __name__ == "__main__":
    main()
