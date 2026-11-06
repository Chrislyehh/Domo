"""Análisis de calibración de temperatura y humedad relativa de los domos.

Compara los domos con el instrumento de referencia durante los tramos
estables de temperatura y humedad. Imprime y guarda las métricas, guarda los
gráficos como PNG y los abre en ventanas o en el visor de gráficos del IDE.

Uso:
    python "Análisis.py"
    python "Análisis.py" --no-mostrar
"""

from __future__ import annotations

import argparse
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd


# Las rutas se resuelven desde la ubicación del script, no desde la terminal.
BASE = Path(__file__).resolve().parent
RUTA_REF = BASE / "17019_segundos_2026-11-03T15-49.dat"
RUTAS_DOMOS = {
    "Domo 1": BASE / "Domo_1" / "20261103.csv",
    "Domo 3": BASE / "Domo_3" / "20261103.csv",
}

NIVELES = [0, 15, 30]  # temperaturas nominales [°C]
TOLERANCIA = 5.0  # ± °C alrededor de cada nivel
VENTANA = "5min"  # ventana centrada para evaluar estabilidad
MAX_VARIACION = 0.8  # máxima diferencia en la ventana [°C]
SENSORES = ["SHT_Temp", "DHT_Temp"]

# Niveles nominales de las soluciones de referencia de humedad. Se usa una
# tolerancia mayor porque el instrumento no alcanzó exactamente esos valores.
NIVELES_HUMEDAD = [11.5, 33.0, 75.0]  # humedad relativa nominal [%]
TOLERANCIA_HUMEDAD = 10.0  # puntos porcentuales alrededor de cada nivel
MAX_VARIACION_HUMEDAD = 2.0  # variación máxima en cinco minutos [%]
SENSORES_HUMEDAD = ["SHT_Hum", "DHT_Hum"]
TOLERANCIA_TIEMPO = "2s"


def leer_datos() -> tuple[pd.DataFrame, dict[str, pd.DataFrame]]:
    """Carga el instrumento de referencia y los archivos de los domos."""
    rutas = [RUTA_REF, *RUTAS_DOMOS.values()]
    faltantes = [str(ruta) for ruta in rutas if not ruta.is_file()]
    if faltantes:
        detalle = "\n  - ".join(faltantes)
        raise FileNotFoundError(f"No se encontraron estos archivos:\n  - {detalle}")

    referencia = pd.read_csv(
        RUTA_REF,
        skiprows=[0] + list(range(2, 22)),
        na_values=["NAN"],
        parse_dates=["TIMESTAMP"],
        index_col="TIMESTAMP",
    ).sort_index()

    domos: dict[str, pd.DataFrame] = {}
    for nombre, ruta in RUTAS_DOMOS.items():
        domos[nombre] = pd.read_csv(
            ruta,
            skiprows=[1],
            parse_dates=["Timestamp"],
            index_col="Timestamp",
        ).sort_index()

    return referencia, domos


def metricas(y_ref: pd.Series, y_med: pd.Series) -> tuple[float, float, float]:
    """Calcula R², MAE y RMSE usando la referencia como valor verdadero."""
    error = y_med - y_ref
    denominador = np.sum((y_ref - y_ref.mean()) ** 2)
    r2 = np.nan if denominador == 0 else 1 - np.sum(error**2) / denominador
    mae = np.mean(np.abs(error))
    rmse = np.sqrt(np.mean(error**2))
    return float(r2), float(mae), float(rmse)


def filtrar_referencia(
    referencia: pd.DataFrame, domos: dict[str, pd.DataFrame]
) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Selecciona los tramos estables cercanos a los niveles nominales."""
    referencia = referencia[referencia["AirT_C"].between(-40, 60)].copy()

    inicio = max(domo.index.min() for domo in domos.values())
    fin = min(domo.index.max() for domo in domos.values())
    referencia = referencia.loc[inicio:fin].copy()
    if referencia.empty:
        raise ValueError("No existe un periodo común entre la referencia y los domos.")

    rolling = referencia["AirT_C"].rolling(VENTANA, center=True)
    referencia["variacion"] = rolling.max() - rolling.min()

    intervalo_mediano = referencia.index.to_series().diff().median()
    if pd.isna(intervalo_mediano) or intervalo_mediano <= pd.Timedelta(0):
        raise ValueError("No hay suficientes datos temporales válidos en la referencia.")

    n_esperado = pd.Timedelta(VENTANA) / intervalo_mediano
    referencia.loc[rolling.count() < 0.9 * n_esperado, "variacion"] = np.nan

    niveles = np.asarray(NIVELES)
    indice_nivel = np.abs(
        referencia["AirT_C"].to_numpy()[:, None] - niveles
    ).argmin(axis=1)
    referencia["nivel"] = niveles[indice_nivel]
    referencia["distancia_nivel"] = np.abs(
        referencia["AirT_C"] - referencia["nivel"]
    )

    estable = referencia[
        (referencia["distancia_nivel"] <= TOLERANCIA)
        & (referencia["variacion"] <= MAX_VARIACION)
    ].copy()
    if estable.empty:
        raise ValueError("El filtro no encontró tramos estables para analizar.")

    return referencia, estable


def grafico_tramos(
    referencia: pd.DataFrame, estable: pd.DataFrame
) -> plt.Figure:
    """Crea el control visual de los tramos seleccionados."""
    figura, eje = plt.subplots(figsize=(12, 3.5), num="Tramos de referencia")
    eje.plot(
        referencia.index,
        referencia["AirT_C"],
        color="0.7",
        linewidth=1,
        label="Referencia (todos los datos)",
    )
    for nivel, grupo in estable.groupby("nivel"):
        eje.scatter(grupo.index, grupo["AirT_C"], s=8, label=f"Tramo {nivel} °C")

    eje.set_ylabel("AirT_C [°C]")
    eje.grid(alpha=0.3)
    eje.legend(fontsize=8)
    eje.set_title("Tramos seleccionados de la referencia")
    figura.tight_layout()
    return figura


def grafico_comparacion(
    estable: pd.DataFrame, domos: dict[str, pd.DataFrame]
) -> tuple[plt.Figure, pd.DataFrame]:
    """Compara cada sensor con la referencia y devuelve sus métricas."""
    marcadores = dict(zip(NIVELES, ["o", "s", "^"]))
    figura, matriz_ejes = plt.subplots(
        1,
        len(domos),
        figsize=(12, 5.5),
        sharex=True,
        sharey=True,
        squeeze=False,
        num="Domos versus referencia",
    )
    ejes = matriz_ejes.ravel()
    resultados: list[dict[str, object]] = []

    for eje, (nombre, domo) in zip(ejes, domos.items()):
        datos = pd.merge_asof(
            estable[["AirT_C", "nivel"]].sort_index(),
            domo[SENSORES].sort_index(),
            left_index=True,
            right_index=True,
            direction="nearest",
            tolerance=pd.Timedelta(TOLERANCIA_TIEMPO),
        ).dropna()
        if datos.empty:
            raise ValueError(f"No fue posible sincronizar datos para {nombre}.")

        for color, sensor in zip(["C0", "C1"], SENSORES):
            r2, mae, rmse = metricas(datos["AirT_C"], datos[sensor])
            resultados.append(
                {
                    "Domo": nombre,
                    "Sensor": sensor,
                    "Nivel": "Todos",
                    "R²": r2,
                    "MAE [°C]": mae,
                    "RMSE [°C]": rmse,
                    "N": len(datos),
                }
            )

            for nivel, grupo in datos.groupby("nivel"):
                _, mae_nivel, rmse_nivel = metricas(grupo["AirT_C"], grupo[sensor])
                resultados.append(
                    {
                        "Domo": nombre,
                        "Sensor": sensor,
                        "Nivel": nivel,
                        "R²": np.nan,
                        "MAE [°C]": mae_nivel,
                        "RMSE [°C]": rmse_nivel,
                        "N": len(grupo),
                    }
                )
                eje.scatter(
                    grupo["AirT_C"],
                    grupo[sensor],
                    s=8,
                    alpha=0.5,
                    color=color,
                    marker=marcadores[int(nivel)],
                )

            eje.scatter(
                [],
                [],
                color=color,
                s=20,
                label=(
                    f"{sensor}  R²={r2:.3f}  "
                    f"MAE={mae:.2f}  RMSE={rmse:.2f}"
                ),
            )

        limites = [min(NIVELES) - TOLERANCIA, max(NIVELES) + TOLERANCIA]
        eje.plot(limites, limites, "k--", linewidth=1, label="1:1")
        eje.set_xlim(limites)
        eje.set_ylim(limites)
        eje.set_aspect("equal")
        eje.set_title(f"{nombre} vs. referencia ({len(datos)} puntos)")
        eje.set_xlabel("Temperatura de referencia AirT_C [°C]")
        eje.grid(alpha=0.3)
        eje.legend(fontsize=8, loc="upper left")

    ejes[0].set_ylabel("Temperatura del domo [°C]")
    figura.tight_layout()
    return figura, pd.DataFrame(resultados).round(3)


def filtrar_humedad(
    referencia: pd.DataFrame, domos: dict[str, pd.DataFrame]
) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Selecciona tramos de humedad estables cercanos a los niveles nominales."""
    # También se valida AirT_C para eliminar el registro anómalo que contiene
    # simultáneamente 145 °C y 0,11 % de humedad.
    referencia = referencia[
        referencia["RH"].between(0, 100)
        & referencia["AirT_C"].between(-40, 60)
    ].copy()

    inicio = max(domo.index.min() for domo in domos.values())
    fin = min(domo.index.max() for domo in domos.values())
    referencia = referencia.loc[inicio:fin].copy()
    if referencia.empty:
        raise ValueError(
            "No existe un periodo común para analizar la humedad relativa."
        )

    rolling = referencia["RH"].rolling(VENTANA, center=True)
    referencia["variacion_humedad"] = rolling.max() - rolling.min()

    intervalo_mediano = referencia.index.to_series().diff().median()
    if pd.isna(intervalo_mediano) or intervalo_mediano <= pd.Timedelta(0):
        raise ValueError("No hay suficientes datos de humedad válidos.")

    n_esperado = pd.Timedelta(VENTANA) / intervalo_mediano
    referencia.loc[
        rolling.count() < 0.9 * n_esperado, "variacion_humedad"
    ] = np.nan

    niveles = np.asarray(NIVELES_HUMEDAD)
    indice_nivel = np.abs(
        referencia["RH"].to_numpy()[:, None] - niveles
    ).argmin(axis=1)
    referencia["nivel_humedad"] = niveles[indice_nivel]
    referencia["distancia_nivel_humedad"] = np.abs(
        referencia["RH"] - referencia["nivel_humedad"]
    )

    estable = referencia[
        (referencia["distancia_nivel_humedad"] <= TOLERANCIA_HUMEDAD)
        & (referencia["variacion_humedad"] <= MAX_VARIACION_HUMEDAD)
    ].copy()
    if estable.empty:
        raise ValueError("El filtro no encontró tramos estables de humedad.")

    return referencia, estable


def grafico_tramos_humedad(
    referencia: pd.DataFrame, estable: pd.DataFrame
) -> plt.Figure:
    """Crea el control visual de los tramos de humedad seleccionados."""
    figura, eje = plt.subplots(
        figsize=(12, 3.5), num="Tramos de humedad de referencia"
    )
    eje.plot(
        referencia.index,
        referencia["RH"],
        color="0.7",
        linewidth=1,
        label="Referencia (todos los datos)",
    )
    for nivel, grupo in estable.groupby("nivel_humedad"):
        eje.scatter(
            grupo.index,
            grupo["RH"],
            s=8,
            label=f"Tramo nominal {nivel:g} %",
        )

    eje.set_ylabel("Humedad de referencia [%]")
    eje.set_ylim(0, 105)
    eje.grid(alpha=0.3)
    eje.legend(fontsize=8)
    eje.set_title("Tramos estables de humedad relativa seleccionados")
    figura.tight_layout()
    return figura


def grafico_comparacion_humedad(
    estable: pd.DataFrame, domos: dict[str, pd.DataFrame]
) -> tuple[plt.Figure, pd.DataFrame]:
    """Compara los sensores de humedad de cada domo con la referencia."""
    marcadores = dict(zip(NIVELES_HUMEDAD, ["o", "s", "^"]))
    figura, matriz_ejes = plt.subplots(
        1,
        len(domos),
        figsize=(12, 5.5),
        sharex=True,
        sharey=True,
        squeeze=False,
        num="Humedad de los domos versus referencia",
    )
    ejes = matriz_ejes.ravel()
    resultados: list[dict[str, object]] = []

    for eje, (nombre, domo) in zip(ejes, domos.items()):
        datos = pd.merge_asof(
            estable[["RH", "nivel_humedad"]].sort_index(),
            domo[SENSORES_HUMEDAD].sort_index(),
            left_index=True,
            right_index=True,
            direction="nearest",
            tolerance=pd.Timedelta(TOLERANCIA_TIEMPO),
        ).dropna()
        if datos.empty:
            raise ValueError(
                f"No fue posible sincronizar datos de humedad para {nombre}."
            )

        for color, sensor in zip(["C0", "C1"], SENSORES_HUMEDAD):
            r2, mae, rmse = metricas(datos["RH"], datos[sensor])
            resultados.append(
                {
                    "Domo": nombre,
                    "Sensor": sensor,
                    "Nivel nominal [%]": "Todos",
                    "R²": r2,
                    "MAE [%]": mae,
                    "RMSE [%]": rmse,
                    "N": len(datos),
                }
            )

            for nivel, grupo in datos.groupby("nivel_humedad"):
                _, mae_nivel, rmse_nivel = metricas(grupo["RH"], grupo[sensor])
                resultados.append(
                    {
                        "Domo": nombre,
                        "Sensor": sensor,
                        "Nivel nominal [%]": nivel,
                        "R²": np.nan,
                        "MAE [%]": mae_nivel,
                        "RMSE [%]": rmse_nivel,
                        "N": len(grupo),
                    }
                )
                eje.scatter(
                    grupo["RH"],
                    grupo[sensor],
                    s=8,
                    alpha=0.5,
                    color=color,
                    marker=marcadores[float(nivel)],
                )

            eje.scatter(
                [],
                [],
                color=color,
                s=20,
                label=(
                    f"{sensor}  R²={r2:.3f}  "
                    f"MAE={mae:.2f}  RMSE={rmse:.2f}"
                ),
            )

        limites = [0, 100]
        eje.plot(limites, limites, "k--", linewidth=1, label="1:1")
        eje.set_xlim(limites)
        eje.set_ylim(limites)
        eje.set_aspect("equal")
        eje.set_title(f"{nombre} vs. referencia ({len(datos)} puntos)")
        eje.set_xlabel("Humedad de referencia RH [%]")
        eje.grid(alpha=0.3)
        eje.legend(fontsize=8, loc="upper left")

    ejes[0].set_ylabel("Humedad medida por el domo [%]")
    figura.tight_layout()
    return figura, pd.DataFrame(resultados).round(3)


def analizar(mostrar: bool = True) -> dict[str, pd.DataFrame]:
    """Ejecuta los análisis de temperatura y humedad y guarda sus resultados."""
    referencia_original, domos = leer_datos()

    referencia_temp, estable_temp = filtrar_referencia(
        referencia_original.copy(), domos
    )
    puntos_temp = estable_temp["nivel"].value_counts().sort_index().to_dict()
    print(f"Puntos de temperatura por nivel: {puntos_temp}")
    figura_tramos_temp = grafico_tramos(referencia_temp, estable_temp)
    figura_comparacion_temp, tabla_temp = grafico_comparacion(
        estable_temp, domos
    )

    referencia_hum, estable_hum = filtrar_humedad(
        referencia_original.copy(), domos
    )
    puntos_hum = (
        estable_hum["nivel_humedad"].value_counts().sort_index().to_dict()
    )
    print(f"Puntos de humedad por nivel nominal: {puntos_hum}")
    figura_tramos_hum = grafico_tramos_humedad(referencia_hum, estable_hum)
    figura_comparacion_hum, tabla_hum = grafico_comparacion_humedad(
        estable_hum, domos
    )

    ruta_tabla_temp = BASE / "resultados_temperatura.csv"
    ruta_tabla_hum = BASE / "resultados_humedad.csv"
    ruta_tramos_temp = BASE / "tramos_temperatura.png"
    ruta_comparacion_temp = BASE / "comparacion_temperatura.png"
    ruta_tramos_hum = BASE / "tramos_humedad.png"
    ruta_comparacion_hum = BASE / "comparacion_humedad.png"

    tabla_temp.to_csv(ruta_tabla_temp, index=False, encoding="utf-8-sig")
    tabla_hum.to_csv(ruta_tabla_hum, index=False, encoding="utf-8-sig")
    figura_tramos_temp.savefig(ruta_tramos_temp, dpi=150, bbox_inches="tight")
    figura_comparacion_temp.savefig(
        ruta_comparacion_temp, dpi=150, bbox_inches="tight"
    )
    figura_tramos_hum.savefig(ruta_tramos_hum, dpi=150, bbox_inches="tight")
    figura_comparacion_hum.savefig(
        ruta_comparacion_hum, dpi=150, bbox_inches="tight"
    )

    print("\nResultados de temperatura:")
    print(tabla_temp.to_string(index=False))
    print("\nResultados de humedad relativa:")
    print(tabla_hum.to_string(index=False))
    print(f"\nTablas guardadas en:\n  - {ruta_tabla_temp}\n  - {ruta_tabla_hum}")
    print(
        "Gráficos guardados en:"
        f"\n  - {ruta_tramos_temp}"
        f"\n  - {ruta_comparacion_temp}"
        f"\n  - {ruta_tramos_hum}"
        f"\n  - {ruta_comparacion_hum}"
    )

    if mostrar:
        # Una sola llamada hace que todas las figuras aparezcan al mismo tiempo.
        plt.show()
    else:
        plt.close("all")

    return {"temperatura": tabla_temp, "humedad": tabla_hum}


def argumentos() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Compara los sensores de los domos con la referencia."
    )
    parser.add_argument(
        "--no-mostrar",
        action="store_true",
        help="guarda los gráficos sin abrir el visor de Matplotlib",
    )
    return parser.parse_args()


if __name__ == "__main__":
    args = argumentos()
    analizar(mostrar=not args.no_mostrar)
