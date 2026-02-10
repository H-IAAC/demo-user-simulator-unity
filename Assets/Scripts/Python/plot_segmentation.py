import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch
from matplotlib.ticker import FuncFormatter, MaxNLocator

PATH = "inferred_clusters.csv"
SEP = "," # "\t" para tsv, "," para csv
TIME_COL = "timestamp"
CLUSTER_COL = "cluster"

# Salvar figura
SAVE_PATH = "../../Plots/segmentation.png" # vazio para não salvar
SAVE_DPI = 150
SAVE_TRANSPARENT = True

# Visual
FIG_WIDTH = 22
FIG_HEIGHT = 3.4
FIGSIZE = (FIG_WIDTH, FIG_HEIGHT)
LANE_Y0 = 0.25
LANE_H  = 0.5
GAP_FRAC = 0.002
ROUNDING_FRAC = 0.25
EDGE_ALPHA = 0.25
LABEL_MIN_W_FRAC = 0.06
FONT_BASE = 20
AXIS_LABEL_FONT_SIZE = 20
TICK_LABEL_FONT_SIZE = 18
EPISODE_LABEL_FONT_SIZE = 18

df = pd.read_csv(PATH, sep=SEP)
df = df[[TIME_COL, CLUSTER_COL]].copy()
df = df.dropna(subset=[TIME_COL, CLUSTER_COL])

df[TIME_COL] = pd.to_numeric(df[TIME_COL], errors="coerce")
df = df.dropna(subset=[TIME_COL]).sort_values(TIME_COL).reset_index(drop=True)
df[CLUSTER_COL] = df[CLUSTER_COL].astype(str)

if len(df) < 2:
    raise RuntimeError("Dados insuficientes (precisa >= 2 linhas válidas).")

t = df[TIME_COL].to_numpy(dtype=np.float64)

med = np.nanmedian(np.abs(t))
if med > 1e17:      # ns
    t_sec = t * 1e-9
elif med > 1e14:    # us
    t_sec = t * 1e-6
elif med > 1e11:    # ms
    t_sec = t * 1e-3
else:               # assume já em segundos
    t_sec = t.copy()

t0 = t_sec[0]
t_rel = t_sec - t0

dt = np.diff(t_rel, prepend=t_rel[0])
t_rel[dt < 0] = np.nan

df["t"] = t_rel
df = df.dropna(subset=["t"]).reset_index(drop=True)
if len(df) < 2:
    raise RuntimeError("Após limpeza de timestamp, sobraram poucos pontos.")

episodes = []
start_t = df.loc[0, "t"]
prev_c = df.loc[0, CLUSTER_COL]
prev_t = df.loc[0, "t"]

for i in range(1, len(df)):
    ti = df.loc[i, "t"]
    ci = df.loc[i, CLUSTER_COL]
    if ci != prev_c:
        if prev_t > start_t:
            episodes.append((start_t, prev_t, prev_c))
        start_t = ti
        prev_c = ci
    prev_t = ti

if prev_t > start_t:
    episodes.append((start_t, prev_t, prev_c))

if not episodes:
    raise RuntimeError("Nenhum episódio com duração > 0.")

t_min = min(s for s, _, _ in episodes)
t_max = max(e for _, e, _ in episodes)
t_range = max(1e-9, t_max - t_min)

gap = GAP_FRAC * t_range
rounding = ROUNDING_FRAC * LANE_H
label_min_w = LABEL_MIN_W_FRAC * t_range

clusters = sorted({c for _, _, c in episodes})
cmap = plt.get_cmap("tab20")
color_map = {c: cmap(i % cmap.N) for i, c in enumerate(clusters)}

fig, ax = plt.subplots(figsize=FIGSIZE)

ax.set_axisbelow(True)
ax.grid(True, axis="x", linestyle="--", linewidth=0.8, alpha=0.55)
ax.grid(False, axis="y")

for i, (s, e, c) in enumerate(episodes, start=1):
    s2 = s + 0.5 * gap
    e2 = e - 0.5 * gap
    if e2 <= s2:
        continue

    w = e2 - s2
    rect = FancyBboxPatch(
        (s2, LANE_Y0),
        w,
        LANE_H,
        boxstyle=f"round,pad=0.02,rounding_size={rounding}",
        linewidth=1.0,
        edgecolor=(0, 0, 0, EDGE_ALPHA),
        facecolor=color_map[c],
    )
    ax.add_patch(rect)

    if w >= label_min_w:
        ax.text(
            s2 + w / 2,
            LANE_Y0 + LANE_H / 2,
            f"Episode {i}",
            ha="center",
            va="center",
            fontsize=EPISODE_LABEL_FONT_SIZE,
            color="white",
            weight="bold",
            clip_on=True,
        )

ax.set_ylim(0, 1)
ax.set_yticks([LANE_Y0 + LANE_H / 2])
ax.set_yticklabels(["Episodes"], fontdict={"fontsize": TICK_LABEL_FONT_SIZE, "fontweight": "bold"})

ax.set_xlim(t_min, t_max)
ax.set_xlabel("time (s)", fontsize=AXIS_LABEL_FONT_SIZE, fontweight="bold")
ax.xaxis.set_major_locator(MaxNLocator(nbins=10))
ax.xaxis.set_major_formatter(FuncFormatter(lambda x, _: f"{x:.1f}"))
ax.tick_params(axis="x", labelsize=TICK_LABEL_FONT_SIZE)
ax.tick_params(axis="y", labelsize=TICK_LABEL_FONT_SIZE)

for lbl in ax.get_xticklabels():
    lbl.set_fontweight("bold")
for lbl in ax.get_yticklabels():
    lbl.set_fontweight("bold")

ax.spines["top"].set_visible(False)
ax.spines["right"].set_visible(False)

plt.tight_layout()
if SAVE_PATH:
    plt.savefig(SAVE_PATH, dpi=SAVE_DPI, bbox_inches="tight", transparent=SAVE_TRANSPARENT)

plt.show()