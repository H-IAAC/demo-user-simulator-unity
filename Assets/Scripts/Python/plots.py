import argparse
import csv
import math
from pathlib import Path
from statistics import median

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--in", dest="inp", default="inferred_clusters.csv", help="Caminho do CSV/TSV")
    p.add_argument("--out-dir", dest="out_dir", default=".", help="Diretório de saída")
    p.add_argument("--sep", choices=["tab", "comma"], default="comma", help="Separador")
    p.add_argument("--width", type=int, default=1920, help="Largura final (px)")
    p.add_argument("--height", type=int, default=600, help="Altura final (px) de cada imagem")
    p.add_argument("--dpi", type=int, default=150, help="DPI do render (afeta qualidade)")
    p.add_argument("--max-points", type=int, default=6000, help="Máximo de pontos plotados (downsample automático)")
    p.add_argument("--bg", choices=["transparent", "white"], default="transparent", help="Fundo da imagem")

    # Tipografia
    p.add_argument("--base-font", type=int, default=18, help="Tamanho base de fonte")
    p.add_argument("--tick-font", type=int, default=16, help="Tamanho fonte dos ticks")
    p.add_argument("--label-font", type=int, default=20, help="Tamanho fonte dos labels dos eixos")
    p.add_argument("--legend-font", type=int, default=16, help="Tamanho fonte da legenda")

    # Unidades (padrão SI)
    p.add_argument("--acc-unit", default="m/s²", help="Unidade da aceleração (padrão: m/s²)")
    p.add_argument("--speed-unit", default="m/s", help="Unidade da velocidade (padrão: m/s)")
    p.add_argument("--time-unit", default="s", help="Unidade do tempo (padrão: s)")
    return p.parse_args()


def find_col_index(fieldnames, candidates):
    lower = [f.strip().lower() for f in fieldnames]
    for c in candidates:
        c = c.lower()
        if c in lower:
            return lower.index(c)
    return -1


def to_float(s):
    if s is None:
        return None
    s = str(s).strip()
    if not s:
        return None
    s = s.replace(",", ".")
    try:
        return float(s)
    except ValueError:
        return None


def infer_timestamp_scale_to_seconds(ts_values):
    vals = [v for v in ts_values if v is not None]
    if len(vals) < 3:
        return 1.0

    med_abs = median([abs(v) for v in vals])

    if med_abs >= 1e17:
        return 1e-9   # ns -> s
    if med_abs >= 1e14:
        return 1e-6   # us -> s
    if med_abs >= 1e11:
        return 1e-3   # ms -> s
    if med_abs >= 1e8:
        return 1.0    # s -> s (epoch em segundos)

    deltas = []
    prev = vals[0]
    for v in vals[1:]:
        d = v - prev
        prev = v
        if d > 0:
            deltas.append(d)
    if not deltas:
        return 1.0

    dt = median(deltas)

    if dt >= 1e6:
        return 1e-9  # ns -> s
    if dt >= 1e3:
        return 1e-6  # us -> s
    if dt >= 1.0:
        return 1e-3  # ms -> s
    return 1.0       # s -> s


def make_ticks(t_sec, max_ticks=8):
    t_min = t_sec[0]
    t_max = t_sec[-1]
    if t_max <= t_min:
        t_max = t_min + 1e-6
    positions = [t_min + (t_max - t_min) * k / (max_ticks - 1) for k in range(max_ticks)]
    return t_min, t_max, positions


def configure_typography(args):
    plt.rcParams.update({
        "font.size": args.base_font,
        "font.weight": "bold",
        "axes.labelweight": "bold",
        "xtick.labelsize": args.tick_font,
        "ytick.labelsize": args.tick_font,
        "legend.fontsize": args.legend_font,
    })


def new_figure(args):
    fig_w_in = args.width / args.dpi
    fig_h_in = args.height / args.dpi
    if args.bg == "transparent":
        return plt.figure(figsize=(fig_w_in, fig_h_in), dpi=args.dpi, facecolor=(0, 0, 0, 0))
    return plt.figure(figsize=(fig_w_in, fig_h_in), dpi=args.dpi)


def style_axes(a):
    a.grid(True, linewidth=0.8)
    for spine in a.spines.values():
        spine.set_linewidth(1.3)
    a.tick_params(width=1.3)


def save_fig(fig, path: Path, args):
    path.parent.mkdir(parents=True, exist_ok=True)
    save_kwargs = dict(dpi=args.dpi)
    if args.bg == "transparent":
        save_kwargs["transparent"] = True
    fig.tight_layout()
    fig.savefig(str(path), **save_kwargs)
    plt.close(fig)


def main():
    args = parse_args()
    configure_typography(args)

    inp = Path(args.inp)
    out_dir = (Path(__file__).parent / ".." / ".." / "Plots").resolve()
    sep = "\t" if args.sep == "tab" else ","

    if not inp.exists():
        raise SystemExit(f"Arquivo não encontrado: {inp}")

    with inp.open("r", encoding="utf-8", newline="") as f:
        reader = csv.reader(f, delimiter=sep)
        rows = list(reader)

    if len(rows) < 2:
        raise SystemExit("Arquivo precisa ter header + dados.")

    header = rows[0]
    data_rows = rows[1:]

    # Mapeamento de colunas
    idx_ts = find_col_index(header, ["timestamp", "time", "t"])
    idx_ax = find_col_index(header, ["acceleration_x", "acc_x", "ax"])
    idx_ay = find_col_index(header, ["acceleration_y", "acc_y", "ay"])
    idx_sx = find_col_index(header, ["speed_x", "vx", "vel_x", "velocity_x"])
    idx_sy = find_col_index(header, ["speed_y", "vy", "vel_y", "velocity_y"])

    missing = []
    for name, idx in [
        ("timestamp", idx_ts),
        ("acceleration_x", idx_ax),
        ("acceleration_y", idx_ay),
        ("speed_x", idx_sx),
        ("speed_y", idx_sy),
    ]:
        if idx < 0:
            missing.append(name)
    if missing:
        raise SystemExit(f"Colunas não encontradas no header: {missing}\nHeader: {header}")

    # Downsample para limitar pontos
    n = len(data_rows)
    stride = max(1, math.ceil(n / max(1, args.max_points)))

    # Coleta timestamps para inferência (com downsample)
    ts_down = []
    for i in range(0, n, stride):
        r = data_rows[i]
        if len(r) < len(header):
            continue
        vts = to_float(r[idx_ts])
        if vts is not None:
            ts_down.append(vts)
    if not ts_down:
        raise SystemExit("Não consegui ler timestamps numéricos na coluna timestamp.")

    scale = infer_timestamp_scale_to_seconds(ts_down)

    ts0 = None
    for r in data_rows:
        if len(r) < len(header):
            continue
        vts = to_float(r[idx_ts])
        if vts is not None:
            ts0 = vts
            break
    if ts0 is None:
        raise SystemExit("Não consegui ler timestamps numéricos na coluna timestamp.")

    # Montar séries
    t_sec = []
    ax = []
    ay = []
    sx = []
    sy = []

    for i in range(0, n, stride):
        r = data_rows[i]
        if len(r) < len(header):
            continue

        vts = to_float(r[idx_ts])
        if vts is None:
            continue

        t = (vts - ts0) * scale
        if t < 0:
            continue

        vax = to_float(r[idx_ax])
        vay = to_float(r[idx_ay])
        vsx = to_float(r[idx_sx])
        vsy = to_float(r[idx_sy])

        t_sec.append(t)
        ax.append(vax if vax is not None else float("nan"))
        ay.append(vay if vay is not None else float("nan"))
        sx.append(vsx if vsx is not None else float("nan"))
        sy.append(vsy if vsy is not None else float("nan"))

    if len(t_sec) < 2:
        raise SystemExit("Poucos pontos válidos após parse/downsample.")

    # Garante monotonicidade (remove pontos que voltam no tempo)
    t_clean, ax_c, ay_c, sx_c, sy_c = [], [], [], [], []
    last_t = -float("inf")
    for ti, axi, ayi, sxi, syi in zip(t_sec, ax, ay, sx, sy):
        if ti >= last_t:
            t_clean.append(ti)
            ax_c.append(axi)
            ay_c.append(ayi)
            sx_c.append(sxi)
            sy_c.append(syi)
            last_t = ti
    t_sec, ax, ay, sx, sy = t_clean, ax_c, ay_c, sx_c, sy_c

    if len(t_sec) < 2:
        raise SystemExit("Após limpeza de monotonicidade, sobraram poucos pontos.")

    # Exibir somente 3 ticks no eixo X: começo (0), meio e final
    t_start = 0.0
    t_end = t_sec[-1]
    mid = (t_start + t_end) / 2.0
    tick_positions = [t_start, mid, t_end]
    tick_labels = [f"{p:.2f}" for p in tick_positions]

    fig1 = new_figure(args)
    a1 = fig1.add_subplot(1, 1, 1)
    a1.plot(t_sec, ax, label="acc_x", linewidth=2.2)
    a1.plot(t_sec, ay, label="acc_y", linewidth=2.2)
    a1.set_xlabel(f"Time ({args.time_unit})")
    a1.set_ylabel(f"Acceleration ({args.acc_unit})")
    a1.set_xlim(t_start, t_end)
    a1.set_xticks(tick_positions)
    a1.set_xticklabels(tick_labels)
    a1.legend(loc="upper right", frameon=True)
    style_axes(a1)

    acc_out = out_dir / "acc_xy.png"
    save_fig(fig1, acc_out, args)

    fig2 = new_figure(args)
    a2 = fig2.add_subplot(1, 1, 1)
    a2.plot(t_sec, sx, label="speed_x", linewidth=2.2)
    a2.plot(t_sec, sy, label="speed_y", linewidth=2.2)
    a2.set_xlabel(f"Time ({args.time_unit})")
    a2.set_ylabel(f"Speed ({args.speed_unit})")
    a2.set_xlim(t_start, t_end)
    a2.set_xticks(tick_positions)
    a2.set_xticklabels(tick_labels)
    a2.legend(loc="upper right", frameon=True)
    style_axes(a2)

    speed_out = out_dir / "speed_xy.png"
    save_fig(fig2, speed_out, args)

    print(
        "OK\n"
        f"  acc:   {acc_out}\n"
        f"  speed: {speed_out}\n"
        f"  pontos: {len(t_sec)} (stride={stride})\n"
        f"  tempo: 0.00{args.time_unit} .. {t_sec[-1]:.3f}{args.time_unit}\n"
        f"  escala timestamp: seconds=(ts-ts0)*{scale}\n"
        f"  imagem: {args.width}x{args.height}px dpi={args.dpi}"
    )


if __name__ == "__main__":
    main()
