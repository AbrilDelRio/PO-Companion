# Developer Time Tracking (Oct–Dic 2026)

`Developer_Time_Tracking_Oct_Dec_2026.xlsx`: control de horas Malcolm / proMX (sin macros ni VBA).

Regenerar:

```bash
python build_time_tracking.py raw.xlsx            # --no-samples para generarlo sin ejemplos
# opcional: recalcular una copia con LibreOffice y copiar los valores en caché
python inject_cached_values.py raw.xlsx raw_recalculado.xlsx Developer_Time_Tracking_Oct_Dec_2026.xlsx
```

Las instrucciones de uso están en la hoja **Instrucciones** del propio archivo.
