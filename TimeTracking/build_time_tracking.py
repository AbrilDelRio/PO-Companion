"""Genera Developer_Time_Tracking_Oct_Dec_2026.xlsx (sin macros, sin VBA).

Uso:  python build_time_tracking.py [ruta_salida.xlsx] [--no-samples]
"""
import re
import sys
from datetime import date, time, timedelta

from openpyxl import Workbook
from openpyxl.chart import BarChart, DoughnutChart, LineChart, Reference
from openpyxl.chart.series import DataPoint
from openpyxl.comments import Comment
from openpyxl.formatting.rule import FormulaRule
from openpyxl.styles import Alignment, Border, Font, PatternFill, Protection, Side
from openpyxl.utils import get_column_letter as L
from openpyxl.workbook.defined_name import DefinedName
from openpyxl.worksheet.datavalidation import DataValidation
from openpyxl.worksheet.table import Table, TableStyleInfo

OUT = next((a for a in sys.argv[1:] if not a.startswith("--")), "Developer_Time_Tracking_Oct_Dec_2026.xlsx")
WITH_SAMPLES = "--no-samples" not in sys.argv

# ---------------------------------------------------------------- constantes
FIRST = 6              # primera fila de datos en Registro de Horas
NROWS = 1500           # filas pre-formuladas
LASTF = FIRST + NROWS - 1
LAST = 5000            # alcance de los nombres definidos (permite extender filas)
P_START, P_END = date(2026, 10, 1), date(2026, 12, 31)
WEEK0 = date(2026, 9, 28)   # lunes de la semana que contiene el 01/10/2026
WEEKS = [WEEK0 + timedelta(days=7 * i) for i in range(14)]
MONTHS = [("Octubre 2026", date(2026, 10, 1), date(2026, 10, 31)),
          ("Noviembre 2026", date(2026, 11, 1), date(2026, 11, 30)),
          ("Diciembre 2026", date(2026, 12, 1), date(2026, 12, 31))]
STATES = ["Draft", "Submitted", "Approved", "Returned", "Recall Requested", "Cancelled"]
DAYS_ES = ["Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom"]

# ---------------------------------------------------------------- paleta
NAVY, NAVY2 = "14213D", "1F3A68"
BLUE, BLUE_L = "2F6FDE", "DCE8FB"          # Malcolm
PMX, PMX_L = "0B7A75", "D5F0EE"            # proMX
DRAFT, DRAFT_L = "17A2B8", "D4F3F7"        # turquesa
SUB, SUB_L = "7B4FD6", "E8DFFB"            # violeta
APP, APP_L = "2E9E5B", "D9F2E3"            # verde
RET, RET_L = "D64545", "FADBDB"            # rojo
REC, REC_L = "B8860B", "F7ECCB"            # Recall Requested (ámbar)
CAN, CAN_L = "8A8F98", "ECEDEF"            # gris
ORANGE, ORANGE_L = "F28C28", "FDE7CF"      # alertas
BG, BG2, WHITE = "F4F6FA", "E9EDF4", "FFFFFF"
INPUT_FILL, INPUT_FONT = "FFFBEA", "1F4FB0"
GRID = "D5DBE5"
STATE_COL = {"Draft": (DRAFT, DRAFT_L), "Submitted": (SUB, SUB_L), "Approved": (APP, APP_L),
             "Returned": (RET, RET_L), "Recall Requested": (REC, REC_L), "Cancelled": (CAN, CAN_L)}

FONT = "Arial"


def fill(c):
    return PatternFill("solid", start_color=c, end_color=c)


def font(size=10, bold=False, color="1B2333", italic=False):
    return Font(name=FONT, size=size, bold=bold, color=color, italic=italic)


thin = Side(style="thin", color=GRID)
BORDER = Border(left=thin, right=thin, top=thin, bottom=thin)
CENTER = Alignment(horizontal="center", vertical="center", wrap_text=True)
LEFT = Alignment(horizontal="left", vertical="center", wrap_text=False)
LEFTW = Alignment(horizontal="left", vertical="center", wrap_text=True)
F_H = "0.00"
F_USD = '"US$ "#,##0.00'
F_PCT = "0.0%"
F_DATE = "dd/mm/yyyy"
F_TIME = "hh:mm"

wb = Workbook()
wb.remove(wb.active)
SHEETS = ["Dashboard", "Registro de Horas", "Calendario Visual", "Control Malcolm", "Control proMX",
          "Octubre 2026", "Noviembre 2026", "Diciembre 2026", "Resumen Trimestral", "Pagos",
          "Configuración", "Instrucciones"]
ws_ = {n: wb.create_sheet(n) for n in SHEETS}
TAB = {"Dashboard": NAVY, "Registro de Horas": BLUE, "Calendario Visual": SUB, "Control Malcolm": BLUE,
       "Control proMX": PMX, "Octubre 2026": DRAFT, "Noviembre 2026": DRAFT, "Diciembre 2026": DRAFT,
       "Resumen Trimestral": NAVY2, "Pagos": APP, "Configuración": CAN, "Instrucciones": CAN}
for n, w in ws_.items():
    w.sheet_properties.tabColor = TAB[n]
    w.sheet_view.showGridLines = False
    w.sheet_view.zoomScale = 90


def q(sheet):
    return "'" + sheet + "'"


def add_name(name, ref):
    wb.defined_names[name] = DefinedName(name, attr_text=ref)


def style_range(ws, rng, **kw):
    for row in ws[rng]:
        for c in row:
            for k, v in kw.items():
                setattr(c, k, v)


def title(ws, text, sub, width_cols=14):
    ws.row_dimensions[1].height = 34
    ws.row_dimensions[2].height = 20
    for col in range(1, width_cols + 1):
        ws.cell(1, col).fill = fill(NAVY)
        ws.cell(2, col).fill = fill(NAVY)
    ws.cell(1, 2, text).font = Font(name=FONT, size=18, bold=True, color=WHITE)
    ws.cell(1, 2).alignment = Alignment(vertical="center")
    ws.cell(2, 2, sub).font = Font(name=FONT, size=10, color="C9D3E6", italic=True)
    ws.cell(2, 2).alignment = Alignment(vertical="top")


def header(ws, row, col, labels, color=NAVY, height=32):
    ws.row_dimensions[row].height = height
    for i, lab in enumerate(labels):
        c = ws.cell(row, col + i, lab)
        c.font = Font(name=FONT, size=10, bold=True, color=WHITE)
        c.fill = fill(color)
        c.alignment = CENTER
        c.border = BORDER


def section(ws, row, col, text, ncols, color=NAVY2):
    for i in range(ncols):
        ws.cell(row, col + i).fill = fill(color)
    c = ws.cell(row, col, text)
    c.font = Font(name=FONT, size=11, bold=True, color=WHITE)
    c.alignment = Alignment(vertical="center")
    ws.row_dimensions[row].height = 22


def cellf(ws, ref, value, fmt=None, bold=False, color="1B2333", bg=None, align=None, size=10, border=True):
    c = ws[ref]
    c.value = value
    c.font = font(size, bold, color)
    if fmt:
        c.number_format = fmt
    if bg:
        c.fill = fill(bg)
    c.alignment = align or (LEFT if isinstance(value, str) and not str(value).startswith("=") else CENTER)
    if border:
        c.border = BORDER
    return c


def mark_input(c, fmt=None):
    c.fill = fill(INPUT_FILL)
    c.font = font(10, False, INPUT_FONT)
    c.border = BORDER
    c.protection = Protection(locked=False)
    if fmt:
        c.number_format = fmt


def protect(ws):
    p = ws.protection
    p.sheet = True
    p.autoFilter = False
    p.sort = False
    p.formatColumns = False
    p.formatRows = False
    p.selectLockedCells = False
    p.selectUnlockedCells = False


def cf(ws, rng, formula, bg=None, fg=None, bold=False, stop=False):
    kw = {}
    if bg:
        kw["fill"] = PatternFill(start_color=bg, end_color=bg, fill_type="solid", bgColor=bg)
    if fg or bold:
        kw["font"] = Font(color=fg, bold=bold)
    ws.conditional_formatting.add(rng, FormulaRule(formula=[formula], stopIfTrue=stop, **kw))


# ====================================================================
# CONFIGURACIÓN
# ====================================================================
cfg = ws_["Configuración"]
title(cfg, "⚙  Configuración", "Celdas amarillas = editables. El resto son listas y cálculos del sistema.", 24)
for col, w in zip("ABCDEFGHIJKLMNOPQRSTUVWX", [2, 34, 16, 14, 2, 14, 12, 2, 2, 28, 2, 12, 18, 6, 22, 6, 16, 12, 12, 2, 14, 24, 18, 14]):
    cfg.column_dimensions[col].width = w

section(cfg, 4, 2, "Parámetros de jornada y facturación", 3)
params = [
    ("Hora inicio jornada", time(9, 0), F_TIME, "CFG_WS", "Inicio del horario habitual (lunes a viernes)."),
    ("Hora fin jornada", time(17, 0), F_TIME, "CFG_WE", "Fin del horario habitual. Todo lo trabajado fuera de [inicio, fin] es hora extra."),
    ("Horas diarias objetivo", 8, "0.00", "CFG_DH", "Horas objetivo por día laborable."),
    ("Horas semanales objetivo (tope)", 40, "0.00", "CFG_WH", "Tope semanal del objetivo (se aplica en los totales semanales)."),
    ("Bloque proMX (minutos)", 30, "0", "CFG_BLK", "proMX exige múltiplos de este bloque en inicio, fin y duración neta."),
    ("Tarifa USD / hora (proMX)", 50, F_USD, "CFG_RATE", "EJEMPLO: reemplazar por tu tarifa real. Solo aplica a proMX."),
    ("Tarifa horas extras USD / hora (proMX)", 75, F_USD, "CFG_OTRATE", "EJEMPLO: reemplazar por tu tarifa real de horas extras proMX."),
    ("Días máximos en Submitted", 7, "0", "CFG_MAXD", "Alerta si un registro supera estos días en Submitted (desde la fecha de envío)."),
    ("Regla de descanso", "Normales primero", None, "CFG_BRK",
     "Como el registro no indica a qué hora fue el descanso, esta regla decide de dónde se descuenta: "
     "'Normales primero' (descanso dentro del horario habitual), 'Extras primero' o 'Proporcional'. "
     "Si conocés el horario exacto, dividí la tarea en dos registros y dejá el descanso en 0."),
    ("Inicio del período", P_START, F_DATE, "CFG_PSTART", "Registros fuera del período se marcan como inválidos."),
    ("Fin del período", P_END, F_DATE, "CFG_PEND", ""),
    ("Fecha de corte (vacío = HOY)", None, F_DATE, "CFG_CUT", "Para calcular la antigüedad de pendientes. Vacío = fecha actual."),
]
for i, (lab, val, fmt, nm, note) in enumerate(params):
    r = 5 + i
    cellf(cfg, f"B{r}", lab, bold=True)
    c = cfg[f"C{r}"]
    c.value = val
    mark_input(c, fmt)
    c.alignment = CENTER
    add_name(nm, f"{q('Configuración')}!$C${r}")
    if note:
        c.comment = Comment(note, "Time Tracking")
    cfg[f"D{r}"].font = font(8, color="6B7280", italic=True)
# helpers (minutos enteros + fecha de corte efectiva)
cfg["D5"] = "=ROUND(CFG_WS*1440,0)"
cfg["D6"] = "=ROUND(CFG_WE*1440,0)"
cfg["D16"] = "=IF(ISNUMBER(CFG_CUT),INT(CFG_CUT),TODAY())"
cfg["D16"].number_format = F_DATE
for r in (5, 6):
    cfg[f"D{r}"].number_format = '0" min"'
add_name("CFG_WSM", f"{q('Configuración')}!$D$5")
add_name("CFG_WEM", f"{q('Configuración')}!$D$6")
add_name("CFG_TODAY", f"{q('Configuración')}!$D$16")
cfg["D4"] = "Sistema"
cfg["D4"].font = font(9, True, WHITE)
cfg["D4"].fill = fill(NAVY2)
cellf(cfg, "B18", "Validación: hora fin > hora inicio (o cruce de medianoche)",
      bold=False, color="6B7280", border=False)
cfg["C18"] = '=IF(AND(ISNUMBER(CFG_WS),ISNUMBER(CFG_WE),CFG_WE>CFG_WS,CFG_BLK>0),"OK","Revisar")'
cfg["C18"].font = font(10, True)

# días laborables
section(cfg, 4, 6, "Días laborables", 2)
for i, d in enumerate(["Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo"]):
    cellf(cfg, f"F{5 + i}", d)
    c = cfg[f"G{5 + i}"]
    c.value = "Sí" if i < 5 else "No"
    mark_input(c)
    c.alignment = CENTER
add_name("CFG_WDAYS", f"{q('Configuración')}!$G$5:$G$11")

# feriados
HROW, HN = 21, 40
section(cfg, 19, 2, "Feriados federales (EE. UU.) y corporativos — tabla editable", 7)
header(cfg, 20, 2, ["Fecha", "Feriado", "Tipo", "No laborable\nMalcolm", "No laborable\nproMX", "Reduce horas\nobjetivo", "Notas"])
cfg.column_dimensions["G"].width = 13
cfg.column_dimensions["H"].width = 30
fer = [(date(2026, 10, 12), "Columbus Day"), (date(2026, 11, 11), "Veterans Day"),
       (date(2026, 11, 26), "Thanksgiving Day"), (date(2026, 12, 25), "Christmas Day")]
for i in range(HN):
    r = HROW + i
    for col in "BCDEFGH":
        mark_input(cfg[f"{col}{r}"])
    cfg[f"B{r}"].number_format = F_DATE
    if i < len(fer):
        d, n = fer[i]
        cfg[f"B{r}"] = d
        cfg[f"C{r}"] = n
        cfg[f"D{r}"] = "Federal"
        cfg[f"E{r}"] = "Sí"
        cfg[f"F{r}"] = "No"
        cfg[f"G{r}"] = "Sí"
        cfg[f"H{r}"] = "proMX: confirmar si es no laborable"
add_name("FER_F", f"{q('Configuración')}!$B${HROW}:$B${HROW + HN - 1}")
add_name("FER_N", f"{q('Configuración')}!$C${HROW}:$C${HROW + HN - 1}")
add_name("FER_NLM", f"{q('Configuración')}!$E${HROW}:$E${HROW + HN - 1}")
add_name("FER_NLP", f"{q('Configuración')}!$F${HROW}:$F${HROW + HN - 1}")
add_name("FER_OBJ", f"{q('Configuración')}!$G${HROW}:$G${HROW + HN - 1}")
cfg[f"B{HROW + HN + 1}"] = ("Nota: 'No laborable' = ese día no tiene horario normal para esa empresa (todo lo trabajado es hora extra). "
                           "Los feriados federales NO se asumen no laborables para proMX: por defecto figuran como 'No' y deben confirmarse.")
cfg[f"B{HROW + HN + 1}"].font = font(9, color="6B7280", italic=True)

# proyectos
section(cfg, 4, 10, "Proyectos (desplegable)", 1)
projects = ["PCF", "Portal Cliente", "Integraciones", "Mantenimiento", "Interno"]
for i in range(30):
    c = cfg[f"J{5 + i}"]
    mark_input(c)
    if i < len(projects):
        c.value = projects[i]
add_name("L_PROY", f"{q('Configuración')}!$J$5:$J$34")

# listas del sistema
section(cfg, 36, 10, "Listas del sistema (no editar)", 14, CAN)
lists = {
    "L": ("Empresas", ["Malcolm", "proMX"], "L_EMP"),
    "M": ("Estados proMX", STATES, "L_EST"),
    "N": ("N/A", ["—"], "L_NA"),
    "O": ("Regla descanso", ["Normales primero", "Extras primero", "Proporcional"], "L_BRK"),
    "P": ("Sí/No", ["Sí", "No"], "L_SN"),
    "W": ("Tipo feriado", ["Federal", "Corporativo"], "L_TFER"),
}
for col, (hdr, vals, nm) in lists.items():
    cfg[f"{col}37"] = hdr
    cfg[f"{col}37"].font = font(9, True)
    for i, v in enumerate(vals):
        cfg[f"{col}{38 + i}"] = v
    add_name(nm, f"{q('Configuración')}!${col}$38:${col}${37 + len(vals)}")
cfg["Q37"], cfg["R37"], cfg["S37"] = "Mes (filtro)", "Desde", "Hasta"
for c in ("Q37", "R37", "S37"):
    cfg[c].font = font(9, True)
cfg["Q38"], cfg["R38"], cfg["S38"] = "Todos", "=CFG_PSTART", "=CFG_PEND"
for i, (lab, s, e) in enumerate(MONTHS):
    cfg[f"Q{39 + i}"], cfg[f"R{39 + i}"], cfg[f"S{39 + i}"] = lab, s, e
for r in range(38, 42):
    cfg[f"R{r}"].number_format = cfg[f"S{r}"].number_format = F_DATE
add_name("L_MES", f"{q('Configuración')}!$Q$38:$Q$41")
add_name("L_MES3", f"{q('Configuración')}!$Q$39:$Q$41")
add_name("L_MESD", f"{q('Configuración')}!$R$38:$R$41")
add_name("L_MESH", f"{q('Configuración')}!$S$38:$S$41")
cfg["U37"] = "Semanas (lunes)"
cfg["U37"].font = font(9, True)
for i, d in enumerate(WEEKS):
    cfg[f"U{38 + i}"] = d
    cfg[f"U{38 + i}"].number_format = F_DATE
add_name("L_SEM", f"{q('Configuración')}!$U$38:$U$51")
cfg["V37"] = "Proyectos usados (filtro)"
cfg["V37"].font = font(9, True)
cfg["V38"] = "Todos"
for i in range(1, 61):
    cfg[f"V{38 + i}"] = f'=IFERROR(INDEX(R_Proy,MATCH({i},R_PSeq,0))&"","")'
add_name("L_PFILT", f"{q('Configuración')}!$V$38:$V$98")
cfg["X37"] = "Períodos de pago"
cfg["X37"].font = font(9, True)
for i, v in enumerate([m[0] for m in MONTHS] + ["Otro"]):
    cfg[f"X{38 + i}"] = v
add_name("L_PER", f"{q('Configuración')}!$X$38:$X$41")

dv = DataValidation(type="list", formula1="=L_BRK", allow_blank=False)
cfg.add_data_validation(dv)
dv.add("C13")
dv = DataValidation(type="list", formula1="=L_SN", allow_blank=False)
cfg.add_data_validation(dv)
dv.add("G5:G11")
dv.add(f"E{HROW}:G{HROW + HN - 1}")
dv = DataValidation(type="list", formula1="=L_TFER", allow_blank=True)
cfg.add_data_validation(dv)
dv.add(f"D{HROW}:D{HROW + HN - 1}")
dv = DataValidation(type="date", operator="between", formula1="DATE(2026,1,1)", formula2="DATE(2027,12,31)",
                    allow_blank=True, error="Ingresá una fecha válida (dd/mm/aaaa).", showErrorMessage=True)
cfg.add_data_validation(dv)
dv.add(f"B{HROW}:B{HROW + HN - 1}")
dv = DataValidation(type="time", operator="between", formula1="0", formula2="0.999305556",
                    error="Ingresá una hora hh:mm.", showErrorMessage=True)
cfg.add_data_validation(dv)
dv.add("C5:C6")
dv = DataValidation(type="whole", operator="between", formula1="1", formula2="240", showErrorMessage=True)
cfg.add_data_validation(dv)
dv.add("C9")
cf(cfg, f"B{HROW}:H{HROW + HN - 1}", f'=AND($B{HROW}<>"",WEEKDAY($B{HROW},2)>5)', bg=CAN_L)
cfg.freeze_panes = "A4"

# ====================================================================
# REGISTRO DE HORAS
# ====================================================================
reg = ws_["Registro de Horas"]
VIS = [("ID", "ID", 7), ("Fecha", "Fecha", 12), ("Dia", "Día", 6), ("Emp", "Empresa", 11),
       ("Proy", "Proyecto", 16), ("Task", "Task ID", 12), ("Desc", "Descripción", 34),
       ("Ini", "Hora inicio", 9), ("Fin", "Hora fin", 9), ("Brk", "Descanso (min)", 10),
       ("Dur", "Duración real (h)", 10), ("Nor", "Horas normales", 10), ("Ext", "Horas extras", 10),
       ("Blq", "Bloques proMX", 9), ("Reg", "Horas registrables proMX", 12), ("Val", "Validación", 30),
       ("Tipo", "Tipo de día", 18), ("Est", "Estado proMX", 16), ("FEnv", "Fecha envío", 12),
       ("FApr", "Fecha aprobación", 12), ("Obs", "Observaciones", 30), ("Age", "Antigüedad (días)", 10),
       ("Alert", "Alerta", 36)]
HELP = ["Used", "sMin", "eMin", "TOK", "Cross", "Gross", "BrkRaw", "Beff", "Net", "WD0", "WD1", "G1", "N1",
        "N2", "NG", "BN", "BEx", "NormM", "ExtM", "NormD0", "ExtD0", "D1", "NC", "Dup", "SAbs", "EAbs", "OKey",
        "SameOv", "CrossOv", "BlkOK", "Err", "Valid", "Cnt", "St", "Hval", "Hcnt", "NorH", "ExtH", "HD0",
        "HD1", "ND0", "ND1", "XD0", "XD1", "SN", "EN", "XOvH", "USD", "BlkV", "ApD", "FMon", "FProj", "FAll",
        "Label", "PSeq", "PSeqM", "PSeqP", "TSeqM", "TSeqP", "PendSeq", "StCode", "Alr"]
COL = {}
for i, (k, _, _) in enumerate(VIS):
    COL[k] = L(i + 1)
HSTART = len(VIS) + 2  # deja una columna separadora
for i, k in enumerate(HELP):
    COL[k] = L(HSTART + i)
for k, c in COL.items():
    add_name("R_" + k, f"{q('Registro de Horas')}!${c}${FIRST}:${c}${LAST}")

ERR_CHAIN = [
    ('NOT(ISNUMBER([Fecha]))', '"Falta fecha o fecha inválida"'),
    ('[Emp]=""', '"Falta empresa"'),
    ('AND([Emp]<>"Malcolm",[Emp]<>"proMX")', '"Empresa inválida"'),
    ('OR(INT([Fecha])<CFG_PSTART,INT([Fecha])>CFG_PEND)', '"Fecha fuera del período"'),
    ('OR(NOT(ISNUMBER([sMin])),NOT(ISNUMBER([eMin])))', '"Falta hora inicio/fin o formato inválido"'),
    ('[TOK]=0', '"Hora inicio = hora fin"'),
    ('[BrkRaw]<0', '"Descanso inválido"'),
    ('[BrkRaw]>=[Gross]', '"Descanso ≥ duración"'),
    ('[Dup]=1', '"Registro duplicado"'),
    ('[SameOv]>0', '"Se superpone con otro registro "&[Emp]'),
    ('[BlkOK]=0', '"inicio, fin y duración neta deben ser múltiplos de "&CFG_BLK&" min"'),
]
err = '""'
for cond, msg in reversed(ERR_CHAIN):
    err = f"IF({cond},{msg},{err})"
err = f'=IF([Used]=0,"",{err})'

NOT_SAME_COMPANY_INTERSECT = ("SUMPRODUCT((R_Emp=\"proMX\")*(R_EN>0)*((R_EN-(R_EN>[EN])*(R_EN-[EN]))>(R_SN+(R_SN<[SN])*([SN]-R_SN)))"
                              "*((R_EN-(R_EN>[EN])*(R_EN-[EN]))-(R_SN+(R_SN<[SN])*([SN]-R_SN))))")

WD = ('IF(AND(INDEX(CFG_WDAYS,WEEKDAY({d},2))="Sí",IF([Emp]="proMX",COUNTIFS(FER_F,{d},FER_NLP,"Sí"),'
      'COUNTIFS(FER_F,{d},FER_NLM,"Sí"))=0),1,0)')
F = {
    # visibles
    "ID": '=IF([Used]=1,ROW()-5,"")',
    "Dia": '=IF(ISNUMBER([Fecha]),CHOOSE(WEEKDAY([Fecha],2),"Lun","Mar","Mié","Jue","Vie","Sáb","Dom"),"")',
    "Dur": '=IF([TOK]=1,ROUND([Net]/60,4),"")',
    "Nor": '=IF([Valid]=1,ROUND([NormM]/60,4),"")',
    "Ext": '=IF([Valid]=1,ROUND([ExtM]/60,4),"")',
    "Blq": '=IF(AND([Emp]="proMX",[TOK]=1),ROUND([Net]/CFG_BLK,2),"")',
    "Reg": '=IF([Emp]="proMX",IF([Valid]=1,ROUND([Net]/60,4),0),"")',
    "Val": '=IF([Used]=0,"",IF([Valid]=1,"OK",IF([Emp]="proMX","Revisar bloques: ","Revisar: ")&[Err]))',
    "Tipo": ('=IF(NOT(ISNUMBER([Fecha])),"",IF(COUNTIFS(FER_F,INT([Fecha]))>0,"Feriado"&IF([WD0]=1," (laborable)"," (no laborable)"),'
             'IF([WD0]=1,"Laborable",IF(WEEKDAY([Fecha],2)>5,"Fin de semana","No laborable"))))'),
    "Age": '=IF(AND([Emp]="proMX",OR([Est]="Submitted",[Est]="Recall Requested"),ISNUMBER([FEnv])),CFG_TODAY-INT([FEnv]),"")',
    "Alert": ('=IF([Valid]=0,"",IF([CrossOv]>0,"⚠ Se superpone con un registro de la otra empresa",'
              'IF([Emp]="Malcolm",IF(COUNTA([Est],[FEnv],[FApr])>0,"Estado/fechas proMX se ignoran en Malcolm",""),'
              'IF([St]="Cancelled","Cancelado: excluido de totales",IF([Est]="","Sin estado: se trata como Draft",'
              'IF([St]="Submitted",IF(ISNUMBER([Age]),IF([Age]>CFG_MAXD,"⚠ "&[Age]&" días en Submitted (máx. "&CFG_MAXD&")",""),"Falta fecha de envío"),'
              'IF([St]="Returned","⚠ Devuelto: corregir y reenviar",IF([St]="Recall Requested","Retirada solicitada: pendiente",'
              'IF(AND([St]="Approved",NOT(ISNUMBER([FApr]))),"Falta fecha de aprobación",'
              'IF(AND(ISNUMBER([FEnv]),ISNUMBER([FApr]),[FApr]<[FEnv]),"Aprobación anterior al envío","")))))))))'),
    # auxiliares
    "Used": '=IF(COUNTA([Fecha],[Emp],[Proy],[Task],[Desc],[Ini],[Fin],[Brk],[Est],[FEnv],[FApr])>0,1,0)',
    "sMin": '=IF(AND(ISNUMBER([Ini]),[Ini]>=0,[Ini]<1),ROUND([Ini]*1440,0),"")',
    "eMin": '=IF(AND(ISNUMBER([Fin]),[Fin]>=0,[Fin]<1),ROUND([Fin]*1440,0),"")',
    "TOK": '=IF(AND(ISNUMBER([Fecha]),[Emp]<>"",ISNUMBER([sMin]),ISNUMBER([eMin])),IF([sMin]=[eMin],0,1),0)',
    "Cross": '=IF([TOK]=1,IF([eMin]<[sMin],1,0),0)',
    "Gross": '=IF([TOK]=1,IF([Cross]=1,1440-[sMin]+[eMin],[eMin]-[sMin]),0)',
    "BrkRaw": '=IF([Brk]="",0,IF(ISNUMBER([Brk]),[Brk],-1))',
    "Beff": '=MIN(MAX(0,[BrkRaw]),[Gross])',
    "Net": '=MAX(0,[Gross]-MAX(0,[BrkRaw]))',
    "WD0": '=IF(ISNUMBER([Fecha]),' + WD.format(d="INT([Fecha])") + ',0)',
    "WD1": '=IF([Cross]=1,' + WD.format(d="INT([Fecha])+1") + ',0)',
    "G1": '=IF([Cross]=1,1440-[sMin],[Gross])',
    "N1": '=IF(AND([TOK]=1,[WD0]=1),MAX(0,MIN(IF([Cross]=1,1440,[eMin]),CFG_WEM)-MAX([sMin],CFG_WSM)),0)',
    "N2": '=IF(AND([Cross]=1,[WD1]=1),MAX(0,MIN([eMin],CFG_WEM)-CFG_WSM),0)',
    "NG": '=[N1]+[N2]',
    "BN": ('=IF(CFG_BRK="Proporcional",IF([Gross]=0,0,[Beff]*[NG]/[Gross]),IF(CFG_BRK="Extras primero",'
           'MIN([NG],MAX(0,[Beff]-([Gross]-[NG]))),MIN([Beff],[NG])))'),
    "BEx": '=[Beff]-[BN]',
    "NormM": '=[NG]-[BN]',
    "ExtM": '=[Gross]-[NG]-[BEx]',
    "NormD0": '=IF([NG]=0,0,[N1]-[BN]*[N1]/[NG])',
    "ExtD0": '=IF([Gross]-[NG]=0,0,([G1]-[N1])-[BEx]*([G1]-[N1])/([Gross]-[NG]))',
    "D1": '=IF([Cross]=1,INT([Fecha])+1,"")',
    "NC": '=IF(AND([Emp]="proMX",[Est]="Cancelled"),0,1)',
    "Dup": ('=IF(AND([TOK]=1,[NC]=1),IF(COUNTIFS([Emp@p],[Emp],[Fecha@p],[Fecha],[sMin@p],[sMin],[eMin@p],[eMin],'
            '[Task@p],[Task]&"",[NC@p],1)>0,1,0),0)'),
    "SAbs": '=IF([TOK]=1,INT([Fecha])*1440+[sMin],"")',
    "EAbs": '=IF([TOK]=1,[SAbs]+[Gross],"")',
    "OKey": '=IF(AND([TOK]=1,[Dup]=0,[NC]=1),1,0)',
    "SameOv": '=IF([OKey]=1,COUNTIFS(R_Emp,[Emp],R_SAbs,"<"&[EAbs],R_EAbs,">"&[SAbs],R_OKey,1)-1,0)',
    "CrossOv": '=IF([OKey]=1,COUNTIFS(R_Emp,"<>"&[Emp],R_SAbs,"<"&[EAbs],R_EAbs,">"&[SAbs],R_OKey,1),0)',
    "BlkOK": ('=IF(AND([TOK]=1,[Emp]="proMX"),IF(AND(MOD([sMin],CFG_BLK)=0,MOD([eMin],CFG_BLK)=0,'
              'MOD([Net],CFG_BLK)=0,[Net]>0),1,0),1)'),
    "Err": err,
    "Valid": '=IF(AND([Used]=1,[Err]=""),1,0)',
    "Cnt": '=IF(AND([Valid]=1,[NC]=1),1,0)',
    "St": '=IF(AND([Emp]="proMX",[Valid]=1),IF([Est]="","Draft",[Est]),"")',
    "Hval": '=IF([Valid]=1,ROUND([Net]/60,6),0)',
    "Hcnt": '=IF([Cnt]=1,ROUND([Net]/60,6),0)',
    "NorH": '=IF([Cnt]=1,ROUND([NormM]/60,6),0)',
    "ExtH": '=IF([Cnt]=1,ROUND([ExtM]/60,6),0)',
    "HD0": '=IF([Cnt]=1,ROUND(([NormD0]+[ExtD0])/60,6),0)',
    "HD1": '=ROUND([Hcnt]-[HD0],6)',
    "ND0": '=IF([Cnt]=1,ROUND([NormD0]/60,6),0)',
    "ND1": '=ROUND([NorH]-[ND0],6)',
    "XD0": '=IF([Cnt]=1,ROUND([ExtD0]/60,6),0)',
    "XD1": '=ROUND([ExtH]-[XD0],6)',
    "SN": '=IF([Cnt]=1,[SAbs],0)',
    "EN": '=IF([Cnt]=1,[EAbs],0)',
    "XOvH": f'=IF(AND([Cnt]=1,[Emp]="Malcolm",[CrossOv]>0),MIN([Hcnt],{NOT_SAME_COMPANY_INTERSECT}/60),0)',
    "USD": '=IF(AND([Cnt]=1,[Emp]="proMX"),ROUND([NorH]*CFG_RATE+[ExtH]*CFG_OTRATE,6),0)',
    "BlkV": '=IF(AND([Valid]=1,[Emp]="proMX"),ROUND([Net]/CFG_BLK,6),0)',
    "ApD": '=IF([St]="Approved",IF(ISNUMBER([FApr]),INT([FApr]),INT([Fecha])),"")',
    "FMon": '=IF(ISNUMBER([Fecha]),IF(DB_MON="Todos",1,IF(AND(INT([Fecha])>=DB_MS,INT([Fecha])<=DB_ME),1,0)),0)',
    "FProj": '=IF(DB_PROJ="Todos",1,IF([Proy]&""=DB_PROJ&"",1,0))',
    "FAll": '=[FMon]*[FProj]',
    "Label": '=IF([Used]=1,[Task]&IF(AND([Task]&""<>"",[Proy]&""<>"")," · ","")&[Proy],"")',
    "PSeq": '=IF(AND([Valid]=1,[Proy]&""<>""),IF(COUNTIFS([Proy@p],[Proy],[Valid@p],1)=0,MAX([PSeq@p])+1,""),"")',
    "PSeqM": ('=IF(AND([Cnt]=1,[Emp]="Malcolm",[Proy]&""<>""),IF(COUNTIFS([Emp@p],"Malcolm",[Proy@p],[Proy],[Cnt@p],1)=0,'
              'MAX([PSeqM@p])+1,""),"")'),
    "PSeqP": ('=IF(AND([Valid]=1,[Emp]="proMX",[Proy]&""<>""),IF(COUNTIFS([Emp@p],"proMX",[Proy@p],[Proy],[Valid@p],1)=0,'
              'MAX([PSeqP@p])+1,""),"")'),
    "TSeqM": ('=IF(AND([Cnt]=1,[Emp]="Malcolm",[Task]&""<>""),IF(COUNTIFS([Emp@p],"Malcolm",[Task@p],[Task],[Cnt@p],1)=0,'
              'MAX([TSeqM@p])+1,""),"")'),
    "TSeqP": ('=IF(AND([Valid]=1,[Emp]="proMX",[Task]&""<>""),IF(COUNTIFS([Emp@p],"proMX",[Task@p],[Task],[Valid@p],1)=0,'
              'MAX([TSeqP@p])+1,""),"")'),
    "PendSeq": ('=IF(AND([Emp]="proMX",[Valid]=1),IF(OR([St]="Draft",[St]="Submitted",[St]="Returned",'
                '[St]="Recall Requested"),MAX([PendSeq@p])+1,""),"")'),
    "StCode": ('=IF([Valid]=0,0,IF([Emp]="Malcolm",1,MATCH([St],{"Draft","Submitted","Approved","Returned",'
               '"Recall Requested","Cancelled"},0)+1))'),
    "Alr": '=IF(AND([St]="Submitted",ISNUMBER([Age])),IF([Age]>CFG_MAXD,1,0),0)',
}
TOKEN = re.compile(r"\[(\w+)(@p)?\]")


def render(tpl, r):
    def sub(m):
        c = COL[m.group(1)]
        if m.group(2):
            return f"${c}${FIRST - 1}:{c}{r - 1}"
        return f"{c}{r}"
    return TOKEN.sub(sub, tpl)


title(reg, "🕒  Registro de Horas", "Carga diaria. Una fila por tarea/tramo. Malcolm y proMX son registros independientes: nunca se copian entre empresas.", len(VIS))
reg["B3"] = "Leyenda:"
reg["B3"].font = font(9, True)
c = reg["C3"]
c.value = "Editable"
mark_input(c)
c.font = font(9, color=INPUT_FONT)
reg["E3"] = "Fórmula (protegida)"
reg["E3"].fill = fill(BG)
reg["E3"].font = font(9)
reg["G3"] = ("Malcolm: precisión al minuto.  proMX: inicio, fin y duración en bloques de 30 min.  "
             "Cruce de medianoche: hora fin menor que hora inicio.")
reg["G3"].font = font(9, color="6B7280", italic=True)
reg["P3"] = "Registros a revisar:"
reg["P3"].font = font(9, True, RET)
reg["Q3"] = f'=COUNTIFS(R_Used,1,R_Valid,0)'
reg["Q3"].font = font(10, True, RET)
reg["R3"] = "Horas Malcolm / proMX:"
reg["R3"].font = font(9, True)
reg["S3"] = '=SUMIFS(R_Hcnt,R_Emp,"Malcolm")'
reg["T3"] = '=SUMIFS(R_Hcnt,R_Emp,"proMX")'
for ref in ("S3", "T3"):
    reg[ref].number_format = F_H
    reg[ref].font = font(10, True)
reg["S3"].font = font(10, True, BLUE)
reg["T3"].font = font(10, True, PMX)

header(reg, FIRST - 1, 1, [h for _, h, _ in VIS], height=42)
for i, (_, _, w) in enumerate(VIS):
    reg.column_dimensions[L(i + 1)].width = w
reg.column_dimensions[L(len(VIS) + 1)].width = 3
for i, k in enumerate(HELP):
    c = reg.cell(FIRST - 1, HSTART + i, "aux_" + k)
    c.font = font(8, True, "6B7280")
    c.fill = fill(BG2)
    reg.column_dimensions[L(HSTART + i)].width = 9
reg.column_dimensions.group(L(HSTART), L(HSTART + len(HELP) - 1), hidden=True, outline_level=1)

INPUTS = {"Fecha", "Emp", "Proy", "Task", "Desc", "Ini", "Fin", "Brk", "Est", "FEnv", "FApr", "Obs"}
FMT = {"Fecha": F_DATE, "Ini": F_TIME, "Fin": F_TIME, "Brk": "0", "Dur": F_H, "Nor": F_H, "Ext": F_H,
       "Reg": F_H, "FEnv": F_DATE, "FApr": F_DATE, "Age": "0", "Blq": "General"}
inp_fill, frm_fill = fill(INPUT_FILL), fill(BG)
inp_font, frm_font = font(10, False, INPUT_FONT), font(10)
for r in range(FIRST, LASTF + 1):
    for k, _, _ in VIS:
        c = reg[f"{COL[k]}{r}"]
        if k in INPUTS:
            c.fill, c.font = inp_fill, inp_font
            c.protection = Protection(locked=False)
        else:
            c.value = render(F[k], r)
            c.fill, c.font = frm_fill, frm_font
        c.border = BORDER
        if k in FMT:
            c.number_format = FMT[k]
        c.alignment = LEFT if k in ("Desc", "Obs", "Val", "Alert", "Proy", "Task", "Tipo") else CENTER
    for k in HELP:
        c = reg[f"{COL[k]}{r}"]
        c.value = render(F[k], r)
        c.font = font(8, color="6B7280")
# filas por debajo de las pre-formuladas: desbloqueadas para poder extender copiando la última fila
for r in range(LASTF + 1, LASTF + 501):
    for i in range(1, HSTART + len(HELP)):
        reg.cell(r, i).protection = Protection(locked=False)

tab = Table(displayName="TablaRegistro", ref=f"A{FIRST - 1}:{L(len(VIS))}{LASTF}")
tab.tableStyleInfo = TableStyleInfo(name="TableStyleLight1", showRowStripes=False)
reg.add_table(tab)
reg.freeze_panes = f"E{FIRST}"

# muestras
SAMPLES = [
    (date(2026, 10, 1), "proMX", "Portal Cliente", "PMX-203", "[EJEMPLO] Desarrollo de funcionalidad", time(9), time(13), 0, "Approved", date(2026, 10, 2), date(2026, 10, 5)),
    (date(2026, 10, 1), "proMX", "Portal Cliente", "PMX-204", "[EJEMPLO] Testing", time(14), time(16), 0, "Approved", date(2026, 10, 2), date(2026, 10, 5)),
    (date(2026, 10, 2), "proMX", "Portal Cliente", "PMX-202", "[EJEMPLO] Implementación", time(9), time(12), 0, "Submitted", date(2026, 10, 2), None),
    (date(2026, 10, 5), "Malcolm", "PCF", "PCF-101", "[EJEMPLO] Desarrollo PCF", time(9), time(12), 0, None, None, None),
    (date(2026, 10, 5), "Malcolm", "PCF", "PCF-102", "[EJEMPLO] Debugging", time(13), time(14, 20), 0, None, None, None),
    (date(2026, 10, 5), "Malcolm", "PCF", "PCF-103", "[EJEMPLO] Horas extras: deploy", time(17), time(19), 0, None, None, None),
    (date(2026, 10, 6), "proMX", "Portal Cliente", "PMX-201", "[EJEMPLO] Corrección de bugs", time(9), time(10, 30), 0, "Draft", None, None),
]
if WITH_SAMPLES:
    for i, s in enumerate(SAMPLES):
        r = FIRST + i
        for k, v in zip(["Fecha", "Emp", "Proy", "Task", "Desc", "Ini", "Fin", "Brk", "Est", "FEnv", "FApr"], s):
            if v is not None:
                reg[f"{COL[k]}{r}"] = v
        reg[f"{COL['Obs']}{r}"] = "EJEMPLO – se puede borrar"

# validaciones
RNG = lambda k: f"{COL[k]}{FIRST}:{COL[k]}{LAST}"
dvs = [
    DataValidation(type="date", operator="between", formula1="CFG_PSTART", formula2="CFG_PEND", allow_blank=True,
                   showErrorMessage=True, errorTitle="Fecha", error="Fecha fuera del período (ver Configuración).",
                   showInputMessage=True, promptTitle="Fecha", prompt="dd/mm/aaaa"), "Fecha",
    DataValidation(type="list", formula1="=L_EMP", allow_blank=True, showErrorMessage=True,
                   error="Elegí Malcolm o proMX."), "Emp",
    DataValidation(type="list", formula1="=L_PROY", allow_blank=True, showErrorMessage=False), "Proy",
    DataValidation(type="time", operator="between", formula1="0", formula2="0.999305556", allow_blank=True,
                   showErrorMessage=True, error="Hora hh:mm entre 00:00 y 23:59.", showInputMessage=True,
                   promptTitle="Hora", prompt="hh:mm  (proMX: :00 o :30)"), "Ini",
    DataValidation(type="time", operator="between", formula1="0", formula2="0.999305556", allow_blank=True,
                   showErrorMessage=True, error="Hora hh:mm entre 00:00 y 23:59. Si cruza medianoche, poné la hora del día siguiente.",
                   showInputMessage=True, promptTitle="Hora fin", prompt="Si es menor que el inicio, se interpreta como el día siguiente."), "Fin",
    DataValidation(type="whole", operator="between", formula1="0", formula2="1440", allow_blank=True,
                   showErrorMessage=True, error="Minutos enteros (0-1440)."), "Brk",
    DataValidation(type="list", formula1=f'=IF($D{FIRST}="proMX",L_EST,L_NA)', allow_blank=True, showErrorMessage=True,
                   error="Solo para proMX: Draft, Submitted, Approved, Returned, Recall Requested o Cancelled."), "Est",
    DataValidation(type="date", operator="between", formula1="DATE(2026,1,1)", formula2="DATE(2027,12,31)",
                   allow_blank=True, showErrorMessage=True, error="Fecha inválida."), "FEnv",
    DataValidation(type="date", operator="between", formula1="DATE(2026,1,1)", formula2="DATE(2027,12,31)",
                   allow_blank=True, showErrorMessage=True, error="Fecha inválida."), "FApr",
]
for i in range(0, len(dvs), 2):
    d, k = dvs[i], dvs[i + 1]
    reg.add_data_validation(d)
    d.add(RNG(k))

# formato condicional
A, LV = FIRST, f"{L(len(VIS))}{LAST}"
cf(reg, f"A{A}:{LV}", f'=AND($D{A}="proMX",$R{A}="Cancelled")', fg=CAN)
cf(reg, f"{COL['Val']}{A}:{COL['Val']}{LAST}", f'=AND(${COL["Val"]}{A}<>"",${COL["Val"]}{A}<>"OK")', bg=RET_L, fg=RET, bold=True)
cf(reg, f"{COL['Val']}{A}:{COL['Val']}{LAST}", f'=${COL["Val"]}{A}="OK"', fg=APP, bold=True)
cf(reg, f"D{A}:D{LAST}", f'=$D{A}="Malcolm"', bg=BLUE_L, fg=BLUE, bold=True)
cf(reg, f"D{A}:D{LAST}", f'=$D{A}="proMX"', bg=PMX_L, fg=PMX, bold=True)
for s in STATES:
    cf(reg, f"R{A}:R{LAST}", f'=AND($D{A}="proMX",$R{A}="{s}")', bg=STATE_COL[s][1], fg=STATE_COL[s][0], bold=True)
cf(reg, f"R{A}:T{LAST}", f'=$D{A}="Malcolm"', bg=CAN_L, fg=CAN)
cf(reg, f"{COL['Tipo']}{A}:{COL['Tipo']}{LAST}", f'=LEFT(${COL["Tipo"]}{A},7)="Feriado"', bg=ORANGE_L, fg="9A4A00", bold=True)
cf(reg, f"{COL['Tipo']}{A}:{COL['Tipo']}{LAST}", f'=OR(${COL["Tipo"]}{A}="Fin de semana",${COL["Tipo"]}{A}="No laborable")', bg=CAN_L)
cf(reg, f"B{A}:C{LAST}", f'=AND(ISNUMBER($B{A}),WEEKDAY($B{A},2)>5)', bg=CAN_L)
cf(reg, f"B{A}:C{LAST}", f'=AND(ISNUMBER($B{A}),COUNTIFS(FER_F,$B{A})>0)', bg=ORANGE_L)
cf(reg, f"{COL['Alert']}{A}:{COL['Alert']}{LAST}", f'=LEFT(${COL["Alert"]}{A},1)="⚠"', bg=ORANGE_L, fg="9A4A00", bold=True)
cf(reg, f"M{A}:M{LAST}", f'=AND(ISNUMBER($M{A}),$M{A}>0)', fg=ORANGE, bold=True)
protect(reg)

# ====================================================================
# DASHBOARD — filtros (se necesitan antes por los nombres DB_*)
# ====================================================================
db = ws_["Dashboard"]
add_name("DB_MON", f"{q('Dashboard')}!$C$4")
add_name("DB_PROJ", f"{q('Dashboard')}!$G$4")


def dsum(col, *crit):
    """SUMIFS con reparto por día (incluye la parte después de medianoche)."""
    c0 = ",".join(crit)
    sep = "," if c0 else ""
    return (f"SUMIFS(R_{col}0{sep}{c0.replace('@DATE', 'R_Fecha')})"
            f"+SUMIFS(R_{col}1{sep}{c0.replace('@DATE', 'R_D1')})")


# ====================================================================
# RESÚMENES MENSUALES
# ====================================================================
MTOT = {}   # mes -> fila total
for (mname, ms, me) in MONTHS:
    w = ws_[mname]
    title(w, f"📅  {mname}", "Resumen diario automático (desde Registro de Horas). Horas repartidas por día real si una tarea cruza medianoche.", 13)
    widths = [12, 7, 24, 11, 11, 12, 11, 11, 11, 11, 11, 11, 12, 4]
    for i, wd in enumerate(widths):
        w.column_dimensions[L(i + 1)].width = wd
    hdr = ["Fecha", "Día", "Tipo de día", "Malcolm (h)", "proMX (h)", "Total imputado (h)", "Horas normales",
           "Horas extras", "Draft (h)", "Submitted (h)", "Approved (h)", "Horas objetivo", "Diferencia vs objetivo", "t"]
    HR = 8
    header(w, HR, 1, hdr)
    r = HR + 1
    d = ms
    week_start_row = r
    rows_w = []
    while d <= me:
        D = f"$A{r}"
        w[f"A{r}"] = d
        w[f"B{r}"] = f'=CHOOSE(WEEKDAY(A{r},2),"Lun","Mar","Mié","Jue","Vie","Sáb","Dom")'
        w[f"C{r}"] = f'=IF(COUNTIFS(FER_F,A{r})>0,"Feriado: "&INDEX(FER_N,MATCH(A{r},FER_F,0)),IF(WEEKDAY(A{r},2)>5,"Fin de semana",IF(INDEX(CFG_WDAYS,WEEKDAY(A{r},2))="Sí","Laborable","No laborable")))'
        w[f"D{r}"] = "=" + dsum("HD", 'R_Emp,"Malcolm"', f"@DATE,{D}")
        w[f"E{r}"] = "=" + dsum("HD", 'R_Emp,"proMX"', f"@DATE,{D}")
        w[f"F{r}"] = f"=D{r}+E{r}"
        w[f"G{r}"] = "=" + dsum("ND", f"@DATE,{D}")
        w[f"H{r}"] = "=" + dsum("XD", f"@DATE,{D}")
        for col, s in zip("IJK", ["Draft", "Submitted", "Approved"]):
            w[f"{col}{r}"] = "=" + dsum("HD", f'R_St,"{s}"', f"@DATE,{D}")
        w[f"L{r}"] = f'=IF(AND(INDEX(CFG_WDAYS,WEEKDAY(A{r},2))="Sí",COUNTIFS(FER_F,A{r},FER_OBJ,"Sí")=0),CFG_DH,0)'
        w[f"M{r}"] = f"=F{r}-L{r}"
        w[f"N{r}"] = "D"
        for i in range(1, 14):
            c = w.cell(r, i)
            c.border = BORDER
            c.font = font(10)
            c.alignment = CENTER if i != 3 else LEFT
            if i >= 4:
                c.number_format = F_H
        w[f"A{r}"].number_format = F_DATE
        r += 1
        if d.weekday() == 6 or d == me:
            # subtotal semanal
            a, b = week_start_row, r - 1
            w[f"A{r}"] = f"Total semana {DAYS_ES[(d - timedelta(days=b - a)).weekday()]} {(d - timedelta(days=b - a)).strftime('%d/%m')} – {d.strftime('%d/%m')}"
            for col in "DEFGHIJK":
                w[f"{col}{r}"] = f"=SUM({col}{a}:{col}{b})"
            w[f"L{r}"] = f"=MIN(CFG_WH,SUM(L{a}:L{b}))"
            w[f"M{r}"] = f"=F{r}-L{r}"
            w[f"N{r}"] = "S"
            w.merge_cells(f"A{r}:C{r}")
            for i in range(1, 14):
                c = w.cell(r, i)
                c.fill = fill(BG2)
                c.font = font(10, True, NAVY)
                c.border = BORDER
                c.alignment = CENTER
                if i >= 4:
                    c.number_format = F_H
            w[f"A{r}"].alignment = LEFT
            rows_w.append(r)
            r += 1
            week_start_row = r
        d += timedelta(days=1)
    tot = r
    w[f"A{tot}"] = f"TOTAL {mname.upper()}"
    w.merge_cells(f"A{tot}:C{tot}")
    for col in "DEFGHIJKL":
        w[f"{col}{tot}"] = f'=SUMIFS({col}{HR + 1}:{col}{tot - 1},$N${HR + 1}:$N${tot - 1},"S")'
    w[f"M{tot}"] = f"=F{tot}-L{tot}"
    w[f"N{tot}"] = "T"
    for i in range(1, 14):
        c = w.cell(tot, i)
        c.fill = fill(NAVY)
        c.font = font(11, True, WHITE)
        c.border = BORDER
        c.alignment = CENTER
        if i >= 4:
            c.number_format = F_H
    w.row_dimensions[tot].height = 24
    MTOT[mname] = tot
    w.column_dimensions["N"].hidden = True
    # KPI strip
    kpis = [("Malcolm", f"=D{tot}", BLUE), ("proMX", f"=E{tot}", PMX), ("Normales", f"=G{tot}", NAVY2),
            ("Extras", f"=H{tot}", ORANGE), ("Approved", f"=K{tot}", APP), ("Objetivo", f"=L{tot}", NAVY)]
    for i, (lab, fml, colr) in enumerate(kpis):
        col = 4 + i
        i = 0
        cl = w.cell(4, col + i, lab)
        cl.font = font(9, True, "6B7280")
        cl.alignment = CENTER
        cv = w.cell(5, col + i, fml)
        cv.font = font(16, True, colr)
        cv.number_format = '0.00" h"'
        cv.alignment = CENTER
        for rr in (4, 5):
            w.cell(rr, col + i).fill = fill(WHITE)
            w.cell(rr, col + i).border = Border(top=Side(style="medium", color=colr) if rr == 4 else None,
                                                left=thin, right=thin, bottom=thin if rr == 5 else None)
        w.column_dimensions[L(col + i)].width = max(w.column_dimensions[L(col + i)].width, 12)
    w.row_dimensions[5].height = 30
    w.cell(4, 2, "Totales del mes").font = font(10, True, NAVY)
    cf(w, f"A{HR + 1}:M{tot - 1}", f'=AND($N{HR + 1}="D",LEFT($C{HR + 1},7)="Feriado")', bg=ORANGE_L)
    cf(w, f"A{HR + 1}:M{tot - 1}", f'=AND($N{HR + 1}="D",OR($C{HR + 1}="Fin de semana",$C{HR + 1}="No laborable"))', bg=CAN_L)
    cf(w, f"M{HR + 1}:M{tot}", f'=AND($N{HR + 1}<>"",M{HR + 1}<0)', fg=RET)
    w.freeze_panes = f"D{HR + 1}"
    protect(w)

# ====================================================================
# RESUMEN TRIMESTRAL
# ====================================================================
rq = ws_["Resumen Trimestral"]
title(rq, "📊  Resumen Trimestral Oct – Dic 2026", "Consolidación automática de los tres meses. Malcolm y proMX se muestran por separado.", 9)
for col, wd in zip("ABCDEFGH", [2, 46, 15, 15, 15, 17, 4, 50]):
    rq.column_dimensions[col].width = wd
header(rq, 4, 2, ["Indicador", "Octubre", "Noviembre", "Diciembre", "Total trimestre"])
QM = {}


def mrange(i):
    _, s, e = MONTHS[i]
    return f"DATE({s.year},{s.month},{s.day})", f"DATE({e.year},{e.month},{e.day})"


def qsum(expr_by_month, label, fmt=F_H, key=None, total="sum", note=None, bold=False, colr=None):
    r = 5 + len(QM)
    rq[f"B{r}"] = label
    for i, (mname, _, _) in enumerate(MONTHS):
        rq[f"{'CDE'[i]}{r}"] = expr_by_month(i, mname)
    if total == "sum":
        rq[f"F{r}"] = f"=SUM(C{r}:E{r})"
    elif total:
        rq[f"F{r}"] = total.replace("#", str(r))
    for col in "BCDEF":
        c = rq[f"{col}{r}"]
        c.border = BORDER
        c.font = font(10, bold or col == "F", colr or "1B2333")
        if col != "B":
            c.number_format = fmt
            c.alignment = CENTER
    rq[f"F{r}"].fill = fill(BG2)
    if note:
        rq[f"H{r}"] = note
        rq[f"H{r}"].font = font(8, color="6B7280", italic=True)
    QM[key or label] = r
    return r


def from_month(col):
    return lambda i, m: f"={q(m)}!{col}{MTOT[m]}"


def by_state(state, col="HD"):
    def f(i, m):
        s, e = mrange(i)
        return f'=SUMIFS(R_{col}0,R_St,"{state}",R_Fecha,">="&{s},R_Fecha,"<="&{e})+SUMIFS(R_{col}1,R_St,"{state}",R_D1,">="&{s},R_D1,"<="&{e})'
    return f


qsum(from_month("D"), "Horas Malcolm", key="mal", colr=BLUE, bold=True)
qsum(from_month("E"), "Horas proMX", key="pmx", colr=PMX, bold=True)
qsum(from_month("F"), "Total horas imputadas (Malcolm + proMX)", key="tot", bold=True)


def ov(i, m):
    s, e = mrange(i)
    return f'=SUMIFS(R_XOvH,R_Fecha,">="&{s},R_Fecha,"<="&{e})'


qsum(ov, "Superposición Malcolm/proMX (h)", key="ovl", colr=ORANGE,
     note="Tiempo en que hubo registros de ambas empresas a la vez (intervalo bruto, por fecha de inicio).")
qsum(lambda i, m: f"={'CDE'[i]}{QM['tot']}-{'CDE'[i]}{QM['ovl']}", "Tiempo cronológico único (h)", key="uniq",
     note="Total imputado menos superposición: horas reales de reloj.")
qsum(from_month("G"), "Horas normales", key="nor")
qsum(from_month("H"), "Horas extras", key="ext", colr=ORANGE)
qsum(from_month("L"), "Horas objetivo", key="obj")
qsum(lambda i, m: f"={'CDE'[i]}{QM['tot']}-{'CDE'[i]}{QM['obj']}", "Diferencia imputado vs objetivo", key="dif")
qsum(from_month("I"), "proMX Draft (h)", key="draft", colr=DRAFT)
qsum(from_month("J"), "proMX Submitted (h)", key="sub", colr=SUB)
qsum(from_month("K"), "proMX Approved (h)", key="app", colr=APP)
qsum(by_state("Returned"), "proMX Returned (h)", key="ret", colr=RET)
qsum(by_state("Recall Requested"), "proMX Recall Requested (h)", key="rec", colr=REC)


def canc(i, m):
    s, e = mrange(i)
    return f'=SUMIFS(R_Hval,R_St,"Cancelled",R_Fecha,">="&{s},R_Fecha,"<="&{e})'


qsum(canc, "proMX Cancelled (h) — excluidas de totales", key="can", colr=CAN)
pct = lambda i, m: (f"=IFERROR({'CDE'[i]}{QM['app']}/({'CDE'[i]}{QM['app']}+{'CDE'[i]}{QM['sub']}+"
                    f"{'CDE'[i]}{QM['ret']}+{'CDE'[i]}{QM['rec']}),0)")
qsum(pct, "% aprobación proMX", fmt=F_PCT, key="pct",
     total=f"=IFERROR(F{5 + len(QM) - 4}/(F{5 + len(QM) - 4}+F{5 + len(QM) - 5}+F{5 + len(QM) - 3}+F{5 + len(QM) - 2}),0)",
     note="Approved / (Approved + Submitted + Returned + Recall Requested).")
# fix total formula of pct with explicit rows
r = QM["pct"]
rq[f"F{r}"] = f"=IFERROR(F{QM['app']}/(F{QM['app']}+F{QM['sub']}+F{QM['ret']}+F{QM['rec']}),0)"


def usd_state(state):
    def f(i, m):
        s, e = mrange(i)
        return f'=SUMIFS(R_USD,R_St,"{state}",R_Fecha,">="&{s},R_Fecha,"<="&{e})'
    return f


qsum(usd_state("Approved"), "USD aprobados (proMX)", fmt=F_USD, key="usdapp", colr=APP)
qsum(lambda i, m: f'=SUMIFS(PAY_AMT,PAY_PER,"{m}")', "USD cobrados (pagos asignados al período)", fmt=F_USD,
     key="usdcob", total='=SUM(PAY_AMT)', note="El total incluye pagos con período 'Otro'.")
qsum(lambda i, m: f"={'CDE'[i]}{QM['usdapp']}-{'CDE'[i]}{QM['usdcob']}", "USD aprobados pendientes de cobro",
     fmt=F_USD, key="usdpend", total=f"=F{5 + len(QM) - 2}-F{5 + len(QM) - 1}")
rq[f"F{QM['usdpend']}"] = f"=F{QM['usdapp']}-F{QM['usdcob']}"
for k in ("mal", "pmx", "tot"):
    pass
rq.freeze_panes = "C5"
QLAST = 4 + len(QM)
cf(rq, f"C{QM['dif']}:F{QM['dif']}", f"=C{QM['dif']}<0", fg=RET)

# gráficos trimestrales
ch = BarChart()
ch.type, ch.grouping, ch.title = "col", "clustered", "Malcolm vs proMX por mes"
ch.y_axis.title = "Horas"
data = Reference(rq, min_col=2, max_col=5, min_row=QM["mal"], max_row=QM["pmx"])
ch.add_data(data, from_rows=True, titles_from_data=True)
ch.set_categories(Reference(rq, min_col=3, max_col=5, min_row=4))
ch.series[0].graphicalProperties.solidFill = BLUE
ch.series[1].graphicalProperties.solidFill = PMX
ch.height, ch.width = 7.5, 15
ch.legend.position = "b"
rq.add_chart(ch, f"B{QLAST + 3}")
ln = LineChart()
ln.title = "Evolución mensual: extras y aprobadas"
for key, colr in (("ext", ORANGE), ("app", APP), ("sub", SUB)):
    ln.add_data(Reference(rq, min_col=2, max_col=5, min_row=QM[key]), from_rows=True, titles_from_data=True)
    ln.series[-1].graphicalProperties.line.solidFill = colr
    ln.series[-1].graphicalProperties.line.width = 28000
ln.set_categories(Reference(rq, min_col=3, max_col=5, min_row=4))
ln.height, ln.width = 7.5, 15
ln.legend.position = "b"
rq.add_chart(ln, f"E{QLAST + 3}")
protect(rq)

# ====================================================================
# PAGOS
# ====================================================================
pg = ws_["Pagos"]
title(pg, "💵  Pagos proMX (USD)", "Solo proMX. Una hora aprobada NO se considera cobrada hasta registrar el pago. Se admiten pagos parciales.", 9)
for col, wd in zip("ABCDEFGHI", [2, 30, 16, 16, 16, 16, 18, 30, 4]):
    pg.column_dimensions[col].width = wd
section(pg, 4, 2, "Tarifas (editar en Configuración)", 2)
cellf(pg, "B5", "Tarifa USD / hora")
cellf(pg, "C5", "=CFG_RATE", F_USD)
cellf(pg, "B6", "Tarifa horas extras USD / hora")
cellf(pg, "C6", "=CFG_OTRATE", F_USD)
section(pg, 8, 2, "Resumen por período (fecha de trabajo)", 6)
header(pg, 9, 2, ["Período", "USD estimados", "USD enviados", "USD aprobados", "USD cobrados", "USD aprobados\npendientes de cobro"])
for i, (mname, s, e) in enumerate(MONTHS):
    r = 10 + i
    sd, ed = mrange(i)
    rng = f'R_Fecha,">="&{sd},R_Fecha,"<="&{ed}'
    cellf(pg, f"B{r}", mname)
    cellf(pg, f"C{r}", f'=SUMIFS(R_USD,{rng})', F_USD)
    cellf(pg, f"D{r}", f'=SUMIFS(R_USD,R_St,"Submitted",{rng})+SUMIFS(R_USD,R_St,"Recall Requested",{rng})+SUMIFS(R_USD,R_St,"Approved",{rng})', F_USD)
    cellf(pg, f"E{r}", f'=SUMIFS(R_USD,R_St,"Approved",{rng})', F_USD)
    cellf(pg, f"F{r}", f'=SUMIFS(PAY_AMT,PAY_PER,B{r})', F_USD)
    cellf(pg, f"G{r}", f"=E{r}-F{r}", F_USD)
cellf(pg, "B13", "Otro / sin período")
for col in "CDE":
    cellf(pg, f"{col}13", 0, F_USD)
cellf(pg, "F13", '=SUMIFS(PAY_AMT,PAY_PER,"Otro")', F_USD)
cellf(pg, "G13", "=E13-F13", F_USD)
cellf(pg, "B14", "TOTAL", bold=True, bg=NAVY, color=WHITE)
for col in "CDEFG":
    cellf(pg, f"{col}14", f"=SUM({col}10:{col}13)", F_USD, bold=True, bg=NAVY, color=WHITE)
pg["B15"] = ("USD estimados = horas proMX válidas no canceladas (Draft, Submitted, Approved, Returned, Recall Requested) × tarifa (normales) "
             "+ extras × tarifa extra.  USD enviados = Submitted + Recall Requested + Approved.  Pendiente negativo = saldo a favor.")
pg["B15"].font = font(8, color="6B7280", italic=True)
pg["B15"].alignment = LEFTW
pg.merge_cells("B15:G16")
pg.row_dimensions[15].height = 22
add_name("PAY_TOT_APP", f"{q('Pagos')}!$E$14")

PR0, PRN = 20, 200
section(pg, 18, 2, "Registro de cobros (editable)", 6)
header(pg, 19, 2, ["Fecha de pago", "Importe USD", "Período", "Referencia de pago", "Medio", "Notas"])
for r in range(PR0, PR0 + PRN):
    for col in "BCDEFG":
        mark_input(pg[f"{col}{r}"])
    pg[f"B{r}"].number_format = F_DATE
    pg[f"C{r}"].number_format = F_USD
if WITH_SAMPLES:
    pg[f"B{PR0}"] = date(2026, 10, 10)
    pg[f"C{PR0}"] = 200
    pg[f"D{PR0}"] = "Octubre 2026"
    pg[f"E{PR0}"] = "EJEMPLO-PAGO-001"
    pg[f"F{PR0}"] = "Transferencia"
    pg[f"G{PR0}"] = "EJEMPLO – pago parcial, se puede borrar"
add_name("PAY_AMT", f"{q('Pagos')}!$C${PR0}:$C${PR0 + PRN - 1}")
add_name("PAY_PER", f"{q('Pagos')}!$D${PR0}:$D${PR0 + PRN - 1}")
add_name("PAY_DATE", f"{q('Pagos')}!$B${PR0}:$B${PR0 + PRN - 1}")
ptab = Table(displayName="TablaPagos", ref=f"B19:G{PR0 + PRN - 1}")
ptab.tableStyleInfo = TableStyleInfo(name="TableStyleLight1", showRowStripes=False)
pg.add_table(ptab)
d1 = DataValidation(type="list", formula1="=L_PER", allow_blank=True, showErrorMessage=True, error="Elegí un período.")
d2 = DataValidation(type="decimal", operator="greaterThan", formula1="0", allow_blank=True, showErrorMessage=True,
                    error="Importe mayor a 0.")
d3 = DataValidation(type="date", operator="between", formula1="DATE(2026,1,1)", formula2="DATE(2027,12,31)",
                    allow_blank=True, showErrorMessage=True, error="Fecha inválida.")
for d_, rg in ((d1, "D"), (d2, "C"), (d3, "B")):
    pg.add_data_validation(d_)
    d_.add(f"{rg}{PR0}:{rg}{PR0 + PRN - 1}")
cf(pg, f"G10:G14", "=G10<0", fg=APP)
cf(pg, f"G10:G13", "=G10>0", fg=ORANGE, bold=True)
pg.freeze_panes = "A4"
protect(pg)

# ====================================================================
# CONTROL MALCOLM
# ====================================================================
cm = ws_["Control Malcolm"]
title(cm, "👤  Control Malcolm", "Solo horas Malcolm (registros válidos). Sin estados de aprobación.", 22)
for col, wd in zip("ABCDEFGHIJKLMNOPQRSTUV", [2, 12, 6, 22, 10, 10, 10, 2, 18, 12, 12, 10, 10, 2, 20, 10, 10, 10, 2, 2, 2, 2]):
    cm.column_dimensions[col].width = wd
cm.column_dimensions["O"].width = 16
cm.column_dimensions["P"].width = 18
kp = [("Total trimestral", '=SUMIFS(R_Hcnt,R_Emp,"Malcolm")', BLUE),
      ("Horas normales", '=SUMIFS(R_NorH,R_Emp,"Malcolm")', NAVY2),
      ("Horas extras", '=SUMIFS(R_ExtH,R_Emp,"Malcolm")', ORANGE),
      ("Registros válidos", '=COUNTIFS(R_Emp,"Malcolm",R_Cnt,1)', NAVY),
      ("Registros a revisar", '=COUNTIFS(R_Emp,"Malcolm",R_Used,1,R_Valid,0)', RET)]
for i, (lab, fml, colr) in enumerate(kp):
    c0 = 2 + i * 3
    cm.merge_cells(start_row=4, start_column=c0, end_row=4, end_column=c0 + 1)
    cm.merge_cells(start_row=5, start_column=c0, end_row=5, end_column=c0 + 1)
    a = cm.cell(4, c0, lab)
    a.font = font(9, True, "6B7280")
    a.alignment = CENTER
    v = cm.cell(5, c0, fml)
    v.font = font(18, True, colr)
    v.alignment = CENTER
    v.number_format = '0.00" h"' if i < 3 else "0"
    for rr in (4, 5):
        for cc in (c0, c0 + 1):
            cm.cell(rr, cc).fill = fill(WHITE)
    cm.cell(4, c0).border = Border(top=Side(style="thick", color=colr))
cm.row_dimensions[5].height = 32

section(cm, 7, 2, "Horas por día", 6, BLUE)
header(cm, 8, 2, ["Fecha", "Día", "Tipo de día", "Horas", "Normales", "Extras"], BLUE)
d = P_START
r = 9
while d <= P_END:
    D = f"$B{r}"
    cm[f"B{r}"] = d
    cm[f"C{r}"] = f'=CHOOSE(WEEKDAY(B{r},2),"Lun","Mar","Mié","Jue","Vie","Sáb","Dom")'
    cm[f"D{r}"] = (f'=IF(COUNTIFS(FER_F,B{r},FER_NLM,"Sí")>0,"Feriado no laborable",IF(COUNTIFS(FER_F,B{r})>0,"Feriado laborable",'
                   f'IF(INDEX(CFG_WDAYS,WEEKDAY(B{r},2))="Sí","Laborable","Fin de semana / no laborable")))')
    cm[f"E{r}"] = "=" + dsum("HD", 'R_Emp,"Malcolm"', f"@DATE,{D}")
    cm[f"F{r}"] = "=" + dsum("ND", 'R_Emp,"Malcolm"', f"@DATE,{D}")
    cm[f"G{r}"] = "=" + dsum("XD", 'R_Emp,"Malcolm"', f"@DATE,{D}")
    for col in "BCDEFG":
        c = cm[f"{col}{r}"]
        c.border = BORDER
        c.font = font(9)
        c.alignment = CENTER if col != "D" else LEFT
        c.number_format = F_DATE if col == "B" else F_H
    r += 1
    d += timedelta(days=1)
CM_DAY_END = r - 1
cellf(cm, f"B{r}", "TOTAL", bold=True, bg=BLUE, color=WHITE)
cm.merge_cells(f"B{r}:D{r}")
for col in "EFG":
    cellf(cm, f"{col}{r}", f"=SUM({col}9:{col}{CM_DAY_END})", F_H, bold=True, bg=BLUE, color=WHITE)
cf(cm, f"B9:G{CM_DAY_END}", '=LEFT($D9,7)="Feriado"', bg=ORANGE_L)
cf(cm, f"B9:G{CM_DAY_END}", '=LEFT($D9,3)="Fin"', bg=CAN_L)
cf(cm, f"E9:E{CM_DAY_END}", "=$E9>0", fg=BLUE, bold=True)

section(cm, 7, 9, "Horas por mes", 5, BLUE)
header(cm, 8, 9, ["Mes", "Horas", "Normales", "Extras", "Registros"], BLUE)
for i, (mname, s, e) in enumerate(MONTHS):
    rr = 9 + i
    sd, ed = mrange(i)
    cellf(cm, f"I{rr}", mname)
    cellf(cm, f"J{rr}", f"=SUMIFS(E$9:E${CM_DAY_END},$B$9:$B${CM_DAY_END},\">=\"&{sd},$B$9:$B${CM_DAY_END},\"<=\"&{ed})", F_H)
    cellf(cm, f"K{rr}", f"=SUMIFS(F$9:F${CM_DAY_END},$B$9:$B${CM_DAY_END},\">=\"&{sd},$B$9:$B${CM_DAY_END},\"<=\"&{ed})", F_H)
    cellf(cm, f"L{rr}", f"=SUMIFS(G$9:G${CM_DAY_END},$B$9:$B${CM_DAY_END},\">=\"&{sd},$B$9:$B${CM_DAY_END},\"<=\"&{ed})", F_H)
    cellf(cm, f"M{rr}", f'=COUNTIFS(R_Emp,"Malcolm",R_Cnt,1,R_Fecha,">="&{sd},R_Fecha,"<="&{ed})', "0")
cellf(cm, "I12", "TOTAL TRIMESTRE", bold=True, bg=BLUE, color=WHITE)
for col in "JKLM":
    cellf(cm, f"{col}12", f"=SUM({col}9:{col}11)", F_H if col != "M" else "0", bold=True, bg=BLUE, color=WHITE)

section(cm, 14, 9, "Horas por semana (lunes a domingo)", 5, BLUE)
header(cm, 15, 9, ["Semana (lunes)", "Hasta (domingo)", "Horas", "Normales", "Extras"], BLUE)
for i, wk in enumerate(WEEKS):
    rr = 16 + i
    cellf(cm, f"I{rr}", wk, F_DATE)
    cellf(cm, f"J{rr}", f"=I{rr}+6", F_DATE)
    for col, src in zip("KLM", "EFG"):
        cellf(cm, f"{col}{rr}", f'=SUMIFS({src}$9:{src}${CM_DAY_END},$B$9:$B${CM_DAY_END},">="&$I{rr},$B$9:$B${CM_DAY_END},"<="&$J{rr})', F_H)
section(cm, 31, 9, "Horas por proyecto", 5, BLUE)
header(cm, 32, 9, ["Proyecto", "Horas", "Normales", "Extras", "% del total"], BLUE)
for k in range(1, 31):
    rr = 32 + k
    cellf(cm, f"I{rr}", f'=IFERROR(INDEX(R_Proy,MATCH({k},R_PSeqM,0))&"","")')
    cellf(cm, f"J{rr}", f'=IF(I{rr}="","",SUMIFS(R_Hcnt,R_Emp,"Malcolm",R_Proy,I{rr}))', F_H)
    cellf(cm, f"K{rr}", f'=IF(I{rr}="","",SUMIFS(R_NorH,R_Emp,"Malcolm",R_Proy,I{rr}))', F_H)
    cellf(cm, f"L{rr}", f'=IF(I{rr}="","",SUMIFS(R_ExtH,R_Emp,"Malcolm",R_Proy,I{rr}))', F_H)
    cellf(cm, f"M{rr}", f'=IF(I{rr}="","",IFERROR(J{rr}/$J$12,0))', F_PCT)
cellf(cm, "I63", "Sin proyecto", color="6B7280")
cellf(cm, "J63", '=SUMIFS(R_Hcnt,R_Emp,"Malcolm",R_Proy,"")', F_H)

section(cm, 7, 15, "Horas por ticket / Task ID", 6, BLUE)
header(cm, 8, 15, ["Task ID", "Proyecto", "Horas", "Normales", "Extras", "Registros"], BLUE)
cm.column_dimensions["Q"].width = 10
cm.column_dimensions["R"].width = 10
cm.column_dimensions["S"].width = 10
cm.column_dimensions["T"].width = 10
for k in range(1, 151):
    rr = 8 + k
    cellf(cm, f"O{rr}", f'=IFERROR(INDEX(R_Task,MATCH({k},R_TSeqM,0))&"","")')
    cellf(cm, f"P{rr}", f'=IFERROR(INDEX(R_Proy,MATCH({k},R_TSeqM,0))&"","")')
    cellf(cm, f"Q{rr}", f'=IF(O{rr}="","",SUMIFS(R_Hcnt,R_Emp,"Malcolm",R_Task,INDEX(R_Task,MATCH({k},R_TSeqM,0))))', F_H)
    cellf(cm, f"R{rr}", f'=IF(O{rr}="","",SUMIFS(R_NorH,R_Emp,"Malcolm",R_Task,INDEX(R_Task,MATCH({k},R_TSeqM,0))))', F_H)
    cellf(cm, f"S{rr}", f'=IF(O{rr}="","",SUMIFS(R_ExtH,R_Emp,"Malcolm",R_Task,INDEX(R_Task,MATCH({k},R_TSeqM,0))))', F_H)
    cellf(cm, f"T{rr}", f'=IF(O{rr}="","",COUNTIFS(R_Emp,"Malcolm",R_Cnt,1,R_Task,INDEX(R_Task,MATCH({k},R_TSeqM,0))))', "0")
cellf(cm, "O159", "Sin Task ID", color="6B7280")
cellf(cm, "Q159", '=SUMIFS(R_Hcnt,R_Emp,"Malcolm",R_Task,"")', F_H)
cm.freeze_panes = "A9"
protect(cm)

# ====================================================================
# CONTROL proMX
# ====================================================================
cp = ws_["Control proMX"]
title(cp, "🏢  Control proMX", "Bloques de 30 min, estados y antigüedad de pendientes. Cada registro cuenta una sola vez, en su estado actual.", 16)
for col, wd in zip("ABCDEFGHIJKLMNOP", [2, 8, 12, 18, 14, 32, 9, 9, 17, 12, 12, 11, 34, 2, 2, 2]):
    cp.column_dimensions[col].width = wd
kp = [("Total horas proMX", '=SUMIFS(R_Hcnt,R_Emp,"proMX")', PMX, '0.00" h"'),
      ("Total bloques", '=SUMIFS(R_BlkV,R_Emp,"proMX")-SUMIFS(R_BlkV,R_St,"Cancelled")', NAVY2, "0"),
      ("Pend. de envío", '=SUMIFS(R_Hval,R_St,"Draft")+SUMIFS(R_Hval,R_St,"Returned")', DRAFT, '0.00" h"'),
      ("Pend. de aprobación", '=SUMIFS(R_Hval,R_St,"Submitted")+SUMIFS(R_Hval,R_St,"Recall Requested")', SUB, '0.00" h"'),
      ("% aprobación", f"=IFERROR(SUMIFS(R_Hval,R_St,\"Approved\")/(SUMIFS(R_Hval,R_St,\"Approved\")+SUMIFS(R_Hval,R_St,\"Submitted\")+SUMIFS(R_Hval,R_St,\"Returned\")+SUMIFS(R_Hval,R_St,\"Recall Requested\")),0)", APP, F_PCT),
      ("Alertas > días máx.", "=SUM(R_Alr)", ORANGE, "0")]
for i, (lab, fml, colr, fmt) in enumerate(kp):
    c0 = 2 + i * 2
    cp.merge_cells(start_row=4, start_column=c0, end_row=4, end_column=c0 + 1)
    cp.merge_cells(start_row=5, start_column=c0, end_row=5, end_column=c0 + 1)
    a = cp.cell(4, c0, lab)
    a.font = font(9, True, "6B7280")
    a.alignment = CENTER
    v = cp.cell(5, c0, fml)
    v.font = font(18, True, colr)
    v.alignment = CENTER
    v.number_format = fmt
    for rr in (4, 5):
        for cc in (c0, c0 + 1):
            cp.cell(rr, cc).fill = fill(WHITE)
    cp.cell(4, c0).border = Border(top=Side(style="thick", color=colr))
cp.row_dimensions[5].height = 32
section(cp, 7, 2, "Horas y bloques por estado", 6, PMX)
header(cp, 8, 2, ["Estado", "", "Horas", "Bloques", "USD", "Registros"], PMX)
cp.merge_cells("B8:C8")
for i, s in enumerate(STATES):
    rr = 9 + i
    cp.merge_cells(f"B{rr}:C{rr}")
    cellf(cp, f"B{rr}", s, bold=True, color=STATE_COL[s][0], bg=STATE_COL[s][1])
    cellf(cp, f"D{rr}", f'=SUMIFS(R_Hval,R_St,"{s}")', F_H)
    cellf(cp, f"E{rr}", f'=SUMIFS(R_BlkV,R_St,"{s}")', "0")
    cellf(cp, f"F{rr}", f'=SUMIFS(R_USD,R_St,"{s}")' if s != "Cancelled" else 0, F_USD)
    cellf(cp, f"G{rr}", f'=COUNTIFS(R_St,"{s}")', "0")
cp.merge_cells("B15:C15")
cellf(cp, "B15", "TOTAL (sin Cancelled)", bold=True, bg=PMX, color=WHITE)
for col, fmt in zip("DEFG", [F_H, "0", F_USD, "0"]):
    cellf(cp, f"{col}15", f"=SUM({col}9:{col}13)", fmt, bold=True, bg=PMX, color=WHITE)

section(cp, 7, 9, "Indicadores", 3, PMX)
ind = [("Horas Draft", "=D9", F_H), ("Horas Submitted", "=D10", F_H), ("Horas Approved", "=D11", F_H),
       ("Horas Returned", "=D12", F_H), ("Horas Recall Requested", "=D13", F_H), ("Horas Cancelled", "=D14", F_H),
       ("Horas pendientes de envío (Draft + Returned)", "=D9+D12", F_H),
       ("Horas pendientes de aprobación (Submitted + Recall)", "=D10+D13", F_H),
       ("Bloques pendientes (envío + aprobación)", "=E9+E10+E12+E13", "0"),
       ("Bloques enviados (Submitted+Approved+Returned+Recall)", "=E10+E11+E12+E13", "0"),
       ("Bloques aprobados", "=E11", "0"),
       ("% aprobación", "=IFERROR(D11/(D11+D10+D12+D13),0)", F_PCT),
       ("Registros a revisar (inválidos)", '=COUNTIFS(R_Emp,"proMX",R_Used,1,R_Valid,0)', "0")]
for i, (lab, fml, fmt) in enumerate(ind):
    rr = 8 + i
    cp.merge_cells(f"I{rr}:K{rr}")
    cellf(cp, f"I{rr}", lab)
    cellf(cp, f"L{rr}", fml, fmt, bold=True)
cp.column_dimensions["I"].width = 17

section(cp, 23, 2, "Horas proMX por mes y estado (por fecha de trabajo)", 11, PMX)
header(cp, 24, 2, ["Mes", "", "Draft", "Submitted", "Approved", "Returned", "Recall Req.", "Cancelled", "Total válido", "Bloques", "% aprob."], PMX)
for i, (mname, s, e) in enumerate(MONTHS):
    rr = 25 + i
    sd, ed = mrange(i)
    rng = f'R_Fecha,">="&{sd},R_Fecha,"<="&{ed}'
    cp.merge_cells(f"B{rr}:C{rr}")
    cellf(cp, f"B{rr}", mname)
    for col, st in zip("DEFGHI", STATES):
        cellf(cp, f"{col}{rr}", f'=SUMIFS(R_Hval,R_St,"{st}",{rng})', F_H)
    cellf(cp, f"J{rr}", f"=SUM(D{rr}:H{rr})", F_H, bold=True)
    cellf(cp, f"K{rr}", f'=SUMIFS(R_BlkV,R_Emp,"proMX",{rng})-SUMIFS(R_BlkV,R_St,"Cancelled",{rng})', "0")
    cellf(cp, f"L{rr}", f"=IFERROR(F{rr}/(F{rr}+E{rr}+G{rr}+H{rr}),0)", F_PCT)
cp.merge_cells("B24:C24")
for col, (st) in zip("DEFGHI", STATES):
    cp[f"{col}24"].fill = fill(STATE_COL[st][0])

section(cp, 29, 2, "Pendientes (Draft, Submitted, Returned, Recall Requested) — antigüedad desde la fecha de envío", 12, PMX)
header(cp, 30, 2, ["ID", "Fecha", "Proyecto", "Task ID", "Descripción", "Horas", "Bloques", "Estado", "Fecha envío",
                   "Fecha aprob.", "Antigüedad", "Alerta"], PMX)
cp.column_dimensions["O"].hidden = True
PEND_N = 200
for k in range(1, PEND_N + 1):
    rr = 30 + k
    cp[f"O{rr}"] = f'=IFERROR(MATCH({k},R_PendSeq,0),"")'
    p = f"$O{rr}"
    fm = {
        "B": f'=IF({p}="","",INDEX(R_ID,{p}))',
        "C": f'=IF({p}="","",INDEX(R_Fecha,{p}))',
        "D": f'=IF({p}="","",INDEX(R_Proy,{p})&"")',
        "E": f'=IF({p}="","",INDEX(R_Task,{p})&"")',
        "F": f'=IF({p}="","",INDEX(R_Desc,{p})&"")',
        "G": f'=IF({p}="","",INDEX(R_Hval,{p}))',
        "H": f'=IF({p}="","",INDEX(R_BlkV,{p}))',
        "I": f'=IF({p}="","",INDEX(R_St,{p}))',
        "J": f'=IF({p}="","",IF(INDEX(R_FEnv,{p})="","",INDEX(R_FEnv,{p})))',
        "K": f'=IF({p}="","",IF(INDEX(R_FApr,{p})="","",INDEX(R_FApr,{p})))',
        "L": f'=IF({p}="","",IF(INDEX(R_Age,{p})="","",INDEX(R_Age,{p})))',
        "M": f'=IF({p}="","",INDEX(R_Alert,{p})&"")',
    }
    for col, fml in fm.items():
        fmt = {"C": F_DATE, "J": F_DATE, "K": F_DATE, "G": F_H, "H": "0", "L": "0", "B": "0"}.get(col)
        cellf(cp, f"{col}{rr}", fml, fmt, size=9)
    cp[f"F{rr}"].alignment = LEFT
    cp[f"M{rr}"].alignment = LEFT
for s in STATES:
    cf(cp, f"I31:I{30 + PEND_N}", f'=$I31="{s}"', bg=STATE_COL[s][1], fg=STATE_COL[s][0], bold=True)
cf(cp, f"L31:M{30 + PEND_N}", '=LEFT($M31,1)="⚠"', bg=ORANGE_L, fg="9A4A00", bold=True)

TR = 30 + PEND_N + 3
section(cp, TR, 2, "Horas proMX por ticket / Task ID", 11, PMX)
header(cp, TR + 1, 2, ["#", "Task ID", "Proyecto", "Horas válidas", "Bloques", "Approved (h)", "Pend. aprob. (h)", "Pend. envío (h)", "Registros", "", ""], PMX)
for k in range(1, 101):
    rr = TR + 1 + k
    t = f"INDEX(R_Task,MATCH({k},R_TSeqP,0))"
    cellf(cp, f"B{rr}", k, "0", size=9)
    cellf(cp, f"C{rr}", f'=IFERROR({t}&"","")', size=9)
    cellf(cp, f"D{rr}", f'=IFERROR(INDEX(R_Proy,MATCH({k},R_TSeqP,0))&"","")', size=9)
    cellf(cp, f"E{rr}", f'=IF(C{rr}="","",SUMIFS(R_Hcnt,R_Emp,"proMX",R_Task,{t}))', F_H, size=9)
    cellf(cp, f"F{rr}", f'=IF(C{rr}="","",SUMIFS(R_BlkV,R_Emp,"proMX",R_Task,{t})-SUMIFS(R_BlkV,R_St,"Cancelled",R_Task,{t}))', "0", size=9)
    cellf(cp, f"G{rr}", f'=IF(C{rr}="","",SUMIFS(R_Hval,R_St,"Approved",R_Task,{t}))', F_H, size=9)
    cellf(cp, f"H{rr}", f'=IF(C{rr}="","",SUMIFS(R_Hval,R_St,"Submitted",R_Task,{t})+SUMIFS(R_Hval,R_St,"Recall Requested",R_Task,{t}))', F_H, size=9)
    cellf(cp, f"I{rr}", f'=IF(C{rr}="","",SUMIFS(R_Hval,R_St,"Draft",R_Task,{t})+SUMIFS(R_Hval,R_St,"Returned",R_Task,{t}))', F_H, size=9)
    cellf(cp, f"J{rr}", f'=IF(C{rr}="","",COUNTIFS(R_Emp,"proMX",R_Valid,1,R_Task,{t}))', "0", size=9)
PR = TR + 104
section(cp, PR, 2, "Horas proMX por proyecto", 11, PMX)
header(cp, PR + 1, 2, ["#", "Proyecto", "", "Horas válidas", "Bloques", "Approved (h)", "Pend. aprob. (h)", "Pend. envío (h)", "USD aprobados", "", ""], PMX)
for k in range(1, 31):
    rr = PR + 1 + k
    pj = f"C{rr}"
    cellf(cp, f"B{rr}", k, "0", size=9)
    cellf(cp, f"C{rr}", f'=IFERROR(INDEX(R_Proy,MATCH({k},R_PSeqP,0))&"","")', size=9)
    cellf(cp, f"E{rr}", f'=IF({pj}="","",SUMIFS(R_Hcnt,R_Emp,"proMX",R_Proy,{pj}))', F_H, size=9)
    cellf(cp, f"F{rr}", f'=IF({pj}="","",SUMIFS(R_BlkV,R_Emp,"proMX",R_Proy,{pj})-SUMIFS(R_BlkV,R_St,"Cancelled",R_Proy,{pj}))', "0", size=9)
    cellf(cp, f"G{rr}", f'=IF({pj}="","",SUMIFS(R_Hval,R_St,"Approved",R_Proy,{pj}))', F_H, size=9)
    cellf(cp, f"H{rr}", f'=IF({pj}="","",SUMIFS(R_Hval,R_St,"Submitted",R_Proy,{pj})+SUMIFS(R_Hval,R_St,"Recall Requested",R_Proy,{pj}))', F_H, size=9)
    cellf(cp, f"I{rr}", f'=IF({pj}="","",SUMIFS(R_Hval,R_St,"Draft",R_Proy,{pj})+SUMIFS(R_Hval,R_St,"Returned",R_Proy,{pj}))', F_H, size=9)
    cellf(cp, f"J{rr}", f'=IF({pj}="","",SUMIFS(R_USD,R_St,"Approved",R_Proy,{pj}))', F_USD, size=9)
cp.freeze_panes = "A6"
protect(cp)

# ====================================================================
# CALENDARIO VISUAL
# ====================================================================
cv = ws_["Calendario Visual"]
title(cv, "🗓  Calendario Visual", "Vista semanal (06:00–23:00, intervalos de 30 min) y vista mensual. Se alimenta de Registro de Horas: no requiere doble carga.", 9)
cv.column_dimensions["A"].width = 8
for i in range(7):
    cv.column_dimensions[L(2 + i)].width = 25
cv.column_dimensions["I"].width = 3
cellf(cv, "A3", "Semana:", bold=True, border=False)
c = cv["B3"]
c.value = date(2026, 10, 5)
mark_input(c, F_DATE)
c.alignment = CENTER
c.comment = Comment("Elegí un lunes de la lista o escribí cualquier fecha: se muestra la semana (lunes a domingo) que la contiene.", "Time Tracking")
dv = DataValidation(type="list", formula1="=L_SEM", allow_blank=False, showErrorMessage=False)
cv.add_data_validation(dv)
dv.add("B3")
cv["C3"] = '=B3-WEEKDAY(B3,2)+1'
cv["C3"].number_format = '"Lunes "dd/mm/yyyy'
cv["C3"].font = font(10, True, NAVY)
cv["D3"] = "=C3+6"
cv["D3"].number_format = '"→ Domingo "dd/mm/yyyy'
cv["D3"].font = font(10, True, NAVY)
add_name("CAL_MON", f"{q('Calendario Visual')}!$C$3")
# leyenda
leg = [("Malcolm", BLUE), ("Draft", DRAFT), ("Submitted", SUB), ("Approved", APP), ("Returned", RET),
       ("Recall Req.", REC), ("⚠ Superposición", ORANGE)]
for i, (lab, colr) in enumerate(leg):
    rr = 3 if i < 4 else 4
    cc = 5 + (i % 4)
    if i >= 4:
        cc = 5 + (i - 4)
    c = cv.cell(rr, cc, lab)
    c.fill = fill(colr)
    c.font = font(9, True, WHITE)
    c.alignment = CENTER
cv["H4"] = "Cancelados e inválidos no se dibujan"
cv["H4"].font = font(8, color="6B7280", italic=True)

header(cv, 6, 1, ["Hora"] + ["Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo"])
cellf(cv, "A7", "Fecha", bold=True, bg=BG2)
cellf(cv, "A8", "Tipo", bold=True, bg=BG2)
cellf(cv, "A9", "Malcolm", bold=True, bg=BLUE_L, color=BLUE)
cellf(cv, "A10", "proMX", bold=True, bg=PMX_L, color=PMX)
cellf(cv, "A11", "Total", bold=True, bg=BG2)
SLOT0, NSLOT = 13, 34
HCOL0 = 11  # columnas auxiliares (K en adelante)
for di in range(7):
    col = L(2 + di)
    cv[f"{col}7"] = f"=CAL_MON+{di}"
    cv[f"{col}7"].number_format = F_DATE
    cv[f"{col}8"] = (f'=IF(COUNTIFS(FER_F,{col}7)>0,"Feriado: "&INDEX(FER_N,MATCH({col}7,FER_F,0)),'
                     f'IF(INDEX(CFG_WDAYS,WEEKDAY({col}7,2))="Sí","Laborable","No laborable"))')
    cv[f"{col}9"] = "=" + dsum("HD", 'R_Emp,"Malcolm"', f"@DATE,{col}$7")
    cv[f"{col}10"] = "=" + dsum("HD", 'R_Emp,"proMX"', f"@DATE,{col}$7")
    cv[f"{col}11"] = f"={col}9+{col}10"
    for rr in range(7, 12):
        c = cv[f"{col}{rr}"]
        c.border = BORDER
        c.alignment = CENTER
        c.font = font(9, rr in (7, 11), {9: BLUE, 10: PMX}.get(rr, "1B2333"))
        if rr >= 9:
            c.number_format = '0.00" h"'
    cf(cv, f"{col}6:{col}8", f'=LEFT({col}$8,7)="Feriado"', bg=ORANGE, fg=WHITE, bold=True)
    cf(cv, f"{col}6:{col}8", f'={col}$8="No laborable"', bg=CAN, fg=WHITE)
cv.row_dimensions[6].height = 22
cv.row_dimensions[12].height = 6
for si in range(NSLOT):
    rr = SLOT0 + si
    mins = 360 + si * 30
    cv[f"A{rr}"] = time(mins // 60, mins % 60)
    cv[f"A{rr}"].number_format = F_TIME
    cv[f"A{rr}"].font = font(9, True, "6B7280")
    cv[f"A{rr}"].alignment = Alignment(horizontal="right", vertical="top")
    cv.row_dimensions[rr].height = 20
    for di in range(7):
        col = L(2 + di)
        base = HCOL0 + di * 5
        cM, iM, cP, iP, code = (L(base + j) for j in range(5))
        T = f"(INT({col}$7)*1440+{mins})"
        crit = f'R_SN,"<"&({T}+30),R_EN,">"&{T}'
        cv[f"{cM}{rr}"] = f'=COUNTIFS(R_Emp,"Malcolm",R_Cnt,1,{crit})'
        cv[f"{iM}{rr}"] = f'=IF({cM}{rr}>0,_xlfn.MINIFS(R_ID,R_Emp,"Malcolm",R_Cnt,1,{crit}),0)'
        cv[f"{cP}{rr}"] = f'=COUNTIFS(R_Emp,"proMX",R_Cnt,1,{crit})'
        cv[f"{iP}{rr}"] = f'=IF({cP}{rr}>0,_xlfn.MINIFS(R_ID,R_Emp,"proMX",R_Cnt,1,{crit}),0)'
        cv[f"{code}{rr}"] = f'=IF(AND({cM}{rr}>0,{cP}{rr}>0),8,IF({cM}{rr}>0,1,IF({cP}{rr}>0,INDEX(R_StCode,{iP}{rr}),0)))'
        first = "TRUE" if si == 0 else "FALSE"
        txtM = (f'IF({cM}{rr}>1,"Malcolm ("&{cM}{rr}&" tareas)",IF(OR({first},INDEX(R_SN,{iM}{rr})>={T}),'
                f'"Malcolm · "&INDEX(R_Label,{iM}{rr})&" ("&TEXT(INDEX(R_Ini,{iM}{rr}),"hh:mm")&"–"&TEXT(INDEX(R_Fin,{iM}{rr}),"hh:mm")&")",""))')
        txtP = (f'IF(OR({first},INDEX(R_SN,{iP}{rr})>={T}),"proMX · "&INDEX(R_Label,{iP}{rr})&" · "&INDEX(R_St,{iP}{rr}),"")')
        cv[f"{col}{rr}"] = (f'=IF({code}{rr}=0,"",IF({code}{rr}=8,"⚠ Malcolm + proMX",'
                            f'IF({code}{rr}=1,{txtM},{txtP})))')
        c = cv[f"{col}{rr}"]
        c.font = font(8, True, WHITE)
        c.alignment = Alignment(horizontal="left", vertical="top", wrap_text=False, shrink_to_fit=False)
        inside = 540 <= mins < 1020
        c.fill = fill("F7F9FC" if inside else WHITE)
        c.border = Border(left=thin, right=thin, top=Side(style="hair", color=GRID), bottom=Side(style="hair", color=GRID))
    # CF por día
for di in range(7):
    col = L(2 + di)
    code = L(HCOL0 + di * 5 + 4)
    rng = f"{col}{SLOT0}:{col}{SLOT0 + NSLOT - 1}"
    for v, colr in ((1, BLUE), (2, DRAFT), (3, SUB), (4, APP), (5, RET), (6, REC), (8, ORANGE)):
        cf(cv, rng, f"=${code}{SLOT0}={v}", bg=colr, fg=WHITE, bold=True)
for cc in range(HCOL0, HCOL0 + 35):
    cv.column_dimensions[L(cc)].width = 6
cv.column_dimensions.group(L(HCOL0), L(HCOL0 + 34), hidden=True, outline_level=1)
cv[f"A{SLOT0 + NSLOT}"] = "Las horas fuera de 06:00–23:00 no se dibujan, pero sí se suman en los totales diarios."
cv[f"A{SLOT0 + NSLOT}"].font = font(8, color="6B7280", italic=True)

# vista mensual
MR = SLOT0 + NSLOT + 3
section(cv, MR, 1, "Vista mensual", 8, NAVY2)
cellf(cv, f"A{MR + 1}", "Mes:", bold=True, border=False)
c = cv[f"B{MR + 1}"]
c.value = "Octubre 2026"
mark_input(c)
c.alignment = CENTER
dv = DataValidation(type="list", formula1="=L_MES3", allow_blank=False)
cv.add_data_validation(dv)
dv.add(f"B{MR + 1}")
cv[f"C{MR + 1}"] = f"=INDEX(L_MESD,MATCH(B{MR + 1},L_MES,0))"
cv[f"D{MR + 1}"] = f"=C{MR + 1}-WEEKDAY(C{MR + 1},2)+1"
cv[f"C{MR + 1}"].number_format = '"Desde "dd/mm/yyyy'
cv[f"D{MR + 1}"].number_format = '"Grilla desde "dd/mm'
cv[f"E{MR + 1}"] = f"=INDEX(L_MESH,MATCH(B{MR + 1},L_MES,0))"
cv[f"E{MR + 1}"].number_format = '"Hasta "dd/mm/yyyy'
for x in "CDE":
    cv[f"{x}{MR + 1}"].font = font(9, color="6B7280")
header(cv, MR + 2, 2, ["Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo"])
G0 = MR + 3
MS_REF, ME_REF, GS = f"$C${MR + 1}", f"$E${MR + 1}", f"$D${MR + 1}"
for wk in range(6):
    r1 = G0 + wk * 4
    cv[f"A{r1}"] = f"Sem {wk + 1}"
    cv[f"A{r1}"].font = font(8, True, "6B7280")
    for di in range(7):
        col = L(2 + di)
        dref = f"{col}{r1}"
        cv[dref] = f"={GS}+{wk * 7 + di}"
        cv[dref].number_format = 'dd" "mmm'
        cv[f"{col}{r1 + 1}"] = (f'=IF(COUNTIFS(FER_F,{dref})>0,INDEX(FER_N,MATCH({dref},FER_F,0)),"")')
        cv[f"{col}{r1 + 2}"] = "=" + dsum("HD", 'R_Emp,"Malcolm"', f"@DATE,{dref}")
        cv[f"{col}{r1 + 3}"] = "=" + dsum("HD", 'R_Emp,"proMX"', f"@DATE,{dref}")
        cv[f"{col}{r1 + 2}"].number_format = '"Malcolm  "0.00" h";;""'
        cv[f"{col}{r1 + 3}"].number_format = '"proMX     "0.00" h";;""'
        cv[dref].font = font(10, True, NAVY)
        cv[f"{col}{r1 + 1}"].font = font(8, True, "9A4A00")
        cv[f"{col}{r1 + 2}"].font = font(9, True, BLUE)
        cv[f"{col}{r1 + 3}"].font = font(9, True, PMX)
        for k in range(4):
            c = cv[f"{col}{r1 + k}"]
            c.alignment = LEFT
            c.border = Border(left=thin, right=thin, top=thin if k == 0 else None, bottom=thin if k == 3 else None)
        rng = f"{col}{r1}:{col}{r1 + 3}"
        cf(cv, rng, f"=OR({dref}<{MS_REF},{dref}>{ME_REF})", bg="F1F2F4", fg="B8BDC6")
        cf(cv, rng, f"=COUNTIFS(FER_F,{dref})>0", bg=ORANGE_L)
        cf(cv, rng, f'=INDEX(CFG_WDAYS,WEEKDAY({dref},2))="No"', bg=BG2)
cv.freeze_panes = "B7"
protect(cv)

# ====================================================================
# DASHBOARD
# ====================================================================
title(db, "Developer Time Tracking · Oct – Dic 2026", "Dashboard vinculado a Registro de Horas. Filtros (celdas amarillas): Mes y Proyecto aplican a los indicadores; los gráficos usan el filtro de Proyecto.", 17)
for i in range(1, 18):
    db.column_dimensions[L(i)].width = 11
db.column_dimensions["A"].width = 2
for col in "EIM":
    db.column_dimensions[col].width = 2
db.column_dimensions["Q"].width = 2
for r in range(3, 76):
    for cc in range(1, 18):
        db.cell(r, cc).fill = fill(BG)
cellf(db, "B4", "Mes ▸", bold=True, color=NAVY, border=False)
db.merge_cells("C4:D4")
c = db["C4"]
c.value = "Todos"
mark_input(c)
c.alignment = CENTER
c.font = font(10, True, INPUT_FONT)
cellf(db, "F4", "Proyecto ▸", bold=True, color=NAVY, border=False)
db.merge_cells("G4:H4")
c = db["G4"]
c.value = "Todos"
mark_input(c)
c.alignment = CENTER
c.font = font(10, True, INPUT_FONT)
dv = DataValidation(type="list", formula1="=L_MES", allow_blank=False, showErrorMessage=True, error="Elegí un mes.")
db.add_data_validation(dv)
dv.add("C4")
dv = DataValidation(type="list", formula1="=L_PFILT", allow_blank=False, showErrorMessage=True,
                    error="Elegí 'Todos' o un proyecto usado en Registro de Horas.")
db.add_data_validation(dv)
dv.add("G4")
db["J4"] = "Período"
db["J4"].font = font(9, color="6B7280")
db["K4"] = "=INDEX(L_MESD,MATCH(DB_MON,L_MES,0))"
db["L4"] = "=INDEX(L_MESH,MATCH(DB_MON,L_MES,0))"
for ref in ("K4", "L4"):
    db[ref].number_format = "dd/mm/yy"
    db[ref].font = font(9, True, NAVY)
    db[ref].alignment = CENTER
add_name("DB_MS", f"{q('Dashboard')}!$K$4")
add_name("DB_ME", f"{q('Dashboard')}!$L$4")
db.merge_cells("N4:P4")
db["N4"] = '=IF(SUMIFS(R_Alr,R_FAll,1)+COUNTIFS(R_St,"Returned",R_FAll,1)>0,"⚠ "&(SUMIFS(R_Alr,R_FAll,1)+COUNTIFS(R_St,"Returned",R_FAll,1))&" alerta(s) proMX","✓ Sin alertas proMX")'
db["N4"].font = font(10, True, ORANGE)
db["N4"].alignment = CENTER
db.row_dimensions[4].height = 24

FA = "R_FAll,1"
FM = "R_FMon,1"
KPIS = [
    ("Total horas trabajadas", f"=SUMIFS(R_Hcnt,{FA})", '0.00" h"', NAVY, "Imputadas a ambas empresas"),
    ("Horas Malcolm", f'=SUMIFS(R_Hcnt,R_Emp,"Malcolm",{FA})', '0.00" h"', BLUE, "Registros válidos"),
    ("Horas proMX", f'=SUMIFS(R_Hcnt,R_Emp,"proMX",{FA})', '0.00" h"', PMX, "Válidas, sin Cancelled"),
    ("Tiempo cronológico único", f"=SUMIFS(R_Hcnt,{FA})-SUMIFS(R_XOvH,{FA})", '0.00" h"', NAVY2,
     f'="Superposición Malcolm/proMX: "&FIXED(SUMIFS(R_XOvH,{FA}),2)&" h"'),
    ("Horas normales", f"=SUMIFS(R_NorH,{FA})", '0.00" h"', NAVY2, "Dentro del horario laboral"),
    ("Horas extras", f"=SUMIFS(R_ExtH,{FA})", '0.00" h"', ORANGE, "Fuera de horario / días no laborables"),
    ("Horas objetivo", f'=IF(DB_MON="Todos",{q("Resumen Trimestral")}!F{QM["obj"]},INDEX({q("Resumen Trimestral")}!C{QM["obj"]}:E{QM["obj"]},MATCH(DB_MON,L_MES3,0)))',
     '0.00" h"', NAVY, '="Cumplimiento (todos los proyectos): "&FIXED(IFERROR(100*SUMIFS(R_Hcnt,R_FMon,1)/[V],0),0)&"%"'),
    ("Horas Draft", f'=SUMIFS(R_Hval,R_St,"Draft",{FA})', '0.00" h"', DRAFT, "proMX · registradas, sin enviar"),
    ("Horas Submitted", f'=SUMIFS(R_Hval,R_St,"Submitted",{FA})', '0.00" h"', SUB, "proMX · enviadas, esperando aprobación"),
    ("Horas Approved", f'=SUMIFS(R_Hval,R_St,"Approved",{FA})', '0.00" h"', APP, "proMX · aprobadas"),
    ("Pendientes de aprobación", f'=SUMIFS(R_Hval,R_St,"Submitted",{FA})+SUMIFS(R_Hval,R_St,"Recall Requested",{FA})',
     '0.00" h"', ORANGE, f'="Submitted + Recall · Pend. envío: "&FIXED(SUMIFS(R_Hval,R_St,"Draft",{FA})+SUMIFS(R_Hval,R_St,"Returned",{FA}),2)&" h"'),
    ("Bloques proMX aprobados", f'=SUMIFS(R_BlkV,R_St,"Approved",{FA})', "0", APP,
     f'="% aprobación: "&FIXED(IFERROR(100*SUMIFS(R_Hval,R_St,"Approved",{FA})/(SUMIFS(R_Hval,R_St,"Approved",{FA})+SUMIFS(R_Hval,R_St,"Submitted",{FA})+SUMIFS(R_Hval,R_St,"Returned",{FA})+SUMIFS(R_Hval,R_St,"Recall Requested",{FA})),0),1)&"%"'),
    ("USD aprobados", f'=SUMIFS(R_USD,R_St,"Approved",{FA})', F_USD, APP, "Horas aprobadas × tarifa"),
    ("USD cobrados", '=IF(DB_MON="Todos",SUM(PAY_AMT),SUMIFS(PAY_AMT,PAY_PER,DB_MON))', F_USD, NAVY2, "Pagos registrados (no filtra proyecto)"),
    ("USD pendientes de cobro", f'=SUMIFS(R_USD,R_St,"Approved",{FM})-IF(DB_MON="Todos",SUM(PAY_AMT),SUMIFS(PAY_AMT,PAY_PER,DB_MON))',
     F_USD, ORANGE, "Aprobado − cobrado (todos los proyectos)"),
    ("Registros a revisar", "=COUNTIFS(R_Used,1,R_Valid,0)", "0", RET, "Inválidos: excluidos de los totales"),
]
CARD_COLS = [2, 6, 10, 14]
KPI_CELL = {}
for i, (lab, fml, fmt, colr, sub) in enumerate(KPIS):
    row0 = 6 + (i // 4) * 5
    c0 = CARD_COLS[i % 4]
    for rr in range(row0, row0 + 4):
        for cc in range(c0, c0 + 3):
            cell = db.cell(rr, cc)
            cell.fill = fill(WHITE)
            cell.border = Border(left=Side(style="thick", color=colr) if cc == c0 else None,
                                 right=thin if cc == c0 + 2 else None,
                                 top=thin if rr == row0 else None, bottom=thin if rr == row0 + 3 else None)
    db.merge_cells(start_row=row0, start_column=c0, end_row=row0, end_column=c0 + 2)
    db.merge_cells(start_row=row0 + 1, start_column=c0, end_row=row0 + 2, end_column=c0 + 2)
    db.merge_cells(start_row=row0 + 3, start_column=c0, end_row=row0 + 3, end_column=c0 + 2)
    a = db.cell(row0, c0, lab.upper())
    a.font = font(8, True, "6B7280")
    a.alignment = Alignment(horizontal="left", vertical="bottom", indent=1)
    v = db.cell(row0 + 1, c0, fml)
    v.font = font(20, True, colr)
    v.number_format = fmt
    v.alignment = Alignment(horizontal="left", vertical="center", indent=1)
    KPI_CELL[lab] = v.coordinate
    s = db.cell(row0 + 3, c0, sub.replace("[V]", v.coordinate))
    s.font = font(8, False, "6B7280", italic=True)
    s.alignment = Alignment(horizontal="left", vertical="top", indent=1)
    db.row_dimensions[row0 + 1].height = 18
for rr in (10, 15, 20, 25):
    db.row_dimensions[rr].height = 8

# ---- datos de gráficos (a la derecha, columnas S en adelante; filtro de Proyecto)
X0 = 19  # S
for cc in range(X0, X0 + 15):
    db.column_dimensions[L(cc)].width = 11
db.column_dimensions[L(X0 + 7)].width = 2
CD = 4
section(db, CD - 1, X0, "Datos de los gráficos (automáticos · filtro de Proyecto)", 14, NAVY2)
FP = "R_FProj,1"
cS, cMal, cPmx, cNor, cExt, cObj, cTot = (L(X0 + k) for k in range(7))
header(db, CD, X0, ["Mes", "Malcolm", "proMX", "Normales", "Extras", "Objetivo", "Trabajadas"], NAVY2, 20)
for i, (mname, s, e) in enumerate(MONTHS):
    rr = CD + 1 + i
    sd, ed = mrange(i)

    def sp(col, extra=""):
        x = f",{extra}" if extra else ""
        return (f'SUMIFS(R_{col}0,{FP}{x},R_Fecha,">="&{sd},R_Fecha,"<="&{ed})'
                f'+SUMIFS(R_{col}1,{FP}{x},R_D1,">="&{sd},R_D1,"<="&{ed})')
    cellf(db, f"{cS}{rr}", mname.split()[0])
    cellf(db, f"{cMal}{rr}", "=" + sp("HD", 'R_Emp,"Malcolm"'), F_H)
    cellf(db, f"{cPmx}{rr}", "=" + sp("HD", 'R_Emp,"proMX"'), F_H)
    cellf(db, f"{cNor}{rr}", "=" + sp("ND"), F_H)
    cellf(db, f"{cExt}{rr}", "=" + sp("XD"), F_H)
    cellf(db, f"{cObj}{rr}", f"={q('Resumen Trimestral')}!{'CDE'[i]}{QM['obj']}", F_H)
    cellf(db, f"{cTot}{rr}", f"={cMal}{rr}+{cPmx}{rr}", F_H)
SD = CD + 6
header(db, SD, X0, ["Estado proMX", "Horas"], NAVY2, 20)
for i, s in enumerate(STATES):
    rr = SD + 1 + i
    cellf(db, f"{cS}{rr}", s)
    cellf(db, f"{cMal}{rr}", f'=SUMIFS(R_Hval,R_St,"{s}",{FP})', F_H)
W0 = X0 + 8  # AA
wS, wMal, wPmx, wObj, wApp, wTot, wLab = (L(W0 + k) for k in range(7))
header(db, CD, W0, ["Semana (lunes)", "Malcolm", "proMX", "Objetivo", "Aprobadas acum.", "Trabajadas", "Etiqueta"], NAVY2, 20)
for i, wk in enumerate(WEEKS):
    rr = CD + 1 + i
    cellf(db, f"{wS}{rr}", wk, "dd/mm")
    wkr = f"{wS}{rr}"

    def spw(col, extra):
        return (f'SUMIFS(R_{col}0,{FP},{extra},R_Fecha,">="&{wkr},R_Fecha,"<="&{wkr}+6)'
                f'+SUMIFS(R_{col}1,{FP},{extra},R_D1,">="&{wkr},R_D1,"<="&{wkr}+6)')
    cellf(db, f"{wMal}{rr}", "=" + spw("HD", 'R_Emp,"Malcolm"'), F_H)
    cellf(db, f"{wPmx}{rr}", "=" + spw("HD", 'R_Emp,"proMX"'), F_H)
    tgt = []
    for (mname, ms, me) in MONTHS:
        tr = MTOT[mname]
        tgt.append(f'SUMIFS({q(mname)}!$L$9:$L${tr - 1},{q(mname)}!$A$9:$A${tr - 1},">="&{wkr},'
                   f'{q(mname)}!$A$9:$A${tr - 1},"<="&{wkr}+6,{q(mname)}!$N$9:$N${tr - 1},"D")')
    cellf(db, f"{wObj}{rr}", f"=MIN(CFG_WH,{'+'.join(tgt)})", F_H)
    cellf(db, f"{wApp}{rr}", f'=SUMIFS(R_Hval,R_St,"Approved",{FP},R_ApD,"<="&{wkr}+6)', F_H)
    cellf(db, f"{wTot}{rr}", f"={wMal}{rr}+{wPmx}{rr}", F_H)
    cellf(db, f"{wLab}{rr}", wk.strftime("%d/%m"))
db.column_dimensions[wS].width = 13
db.column_dimensions[wApp].width = 14

CH_ROW = 27
NW = 14


def place(chart, anchor, w=12.6, h=7.6):
    chart.width, chart.height = w, h
    chart.legend.position = "b"
    db.add_chart(chart, anchor)


def ref(c0, r0, c1=None, r1=None):
    return Reference(db, min_col=c0, min_row=r0, max_col=c1 or c0, max_row=r1 or r0)


c1 = BarChart()
c1.type, c1.grouping, c1.title = "col", "clustered", "Malcolm vs proMX por mes"
c1.add_data(ref(X0 + 1, CD, X0 + 2, CD + 3), titles_from_data=True)
c1.set_categories(ref(X0, CD + 1, X0, CD + 3))
c1.series[0].graphicalProperties.solidFill = BLUE
c1.series[1].graphicalProperties.solidFill = PMX
c1.y_axis.title = "Horas"
place(c1, f"B{CH_ROW}")
c2 = BarChart()
c2.type, c2.grouping, c2.overlap, c2.gapWidth, c2.title = "col", "stacked", 100, 60, "Horas trabajadas por semana"
c2.add_data(ref(W0 + 1, CD, W0 + 2, CD + NW), titles_from_data=True)
c2.set_categories(ref(W0 + 6, CD + 1, W0 + 6, CD + NW))
c2.series[0].graphicalProperties.solidFill = BLUE
c2.series[1].graphicalProperties.solidFill = PMX
place(c2, f"J{CH_ROW}")
c3 = BarChart()
c3.type, c3.grouping, c3.overlap, c3.title = "col", "stacked", 100, "Horas normales vs extras"
c3.add_data(ref(X0 + 3, CD, X0 + 4, CD + 3), titles_from_data=True)
c3.set_categories(ref(X0, CD + 1, X0, CD + 3))
c3.series[0].graphicalProperties.solidFill = NAVY2
c3.series[1].graphicalProperties.solidFill = ORANGE
place(c3, f"B{CH_ROW + 16}")
c4 = DoughnutChart()
c4.title = "Distribución de estados proMX (horas)"
c4.add_data(ref(X0 + 1, SD + 1, X0 + 1, SD + 6), titles_from_data=False)
c4.set_categories(ref(X0, SD + 1, X0, SD + 6))
c4.holeSize = 55
for i, s in enumerate(STATES):
    pt = DataPoint(idx=i)
    pt.graphicalProperties.solidFill = STATE_COL[s][0]
    c4.series[0].dPt.append(pt)
place(c4, f"J{CH_ROW + 16}")
c5 = LineChart()
c5.title = "Evolución de horas aprobadas (acumulado)"
c5.add_data(ref(W0 + 4, CD, W0 + 4, CD + NW), titles_from_data=True)
c5.set_categories(ref(W0 + 6, CD + 1, W0 + 6, CD + NW))
c5.series[0].graphicalProperties.line.solidFill = APP
c5.series[0].graphicalProperties.line.width = 32000
c5.series[0].smooth = False
place(c5, f"B{CH_ROW + 32}")
c6 = BarChart()
c6.type, c6.grouping, c6.gapWidth, c6.title = "col", "clustered", 60, "Horas objetivo vs trabajadas (por semana)"
c6.add_data(ref(W0 + 3, CD, W0 + 3, CD + NW), titles_from_data=True)
c6.set_categories(ref(W0 + 6, CD + 1, W0 + 6, CD + NW))
c6.series[0].graphicalProperties.solidFill = "C9D3E6"
l6 = LineChart()
l6.add_data(ref(W0 + 5, CD, W0 + 5, CD + NW), titles_from_data=True)
l6.series[0].graphicalProperties.line.solidFill = NAVY
l6.series[0].graphicalProperties.line.width = 32000
l6.series[0].smooth = False
c6 += l6
place(c6, f"J{CH_ROW + 32}")
db.freeze_panes = "A5"
protect(db)

# ====================================================================
# INSTRUCCIONES
# ====================================================================
ins = ws_["Instrucciones"]
title(ins, "📘  Instrucciones", "Guía rápida de uso diario.", 3)
ins.column_dimensions["A"].width = 2
ins.column_dimensions["B"].width = 34
ins.column_dimensions["C"].width = 110
TXT = [
    ("Cargar una tarea Malcolm",
     "Registro de Horas → primera fila vacía. Completá Fecha, Empresa = Malcolm, Proyecto, Task ID, Descripción, Hora inicio y Hora fin (precisión al minuto, ej. 13:00–14:20) y Descanso (min) si corresponde. "
     "No completes Estado ni fechas proMX: para Malcolm se ignoran y quedan en gris. Duración, normales y extras se calculan solas."),
    ("Cargar una tarea proMX",
     "Igual que Malcolm pero con Empresa = proMX. Elegí el Estado (Draft si todavía no lo cargaste en proMX). "
     "Si dejás el Estado vacío se trata como Draft y se muestra un aviso."),
    ("Bloques de media hora (proMX)",
     "Inicio y fin deben caer en :00 o :30 y la duración neta (fin − inicio − descanso) debe ser múltiplo de 30 min. "
     "Bloques = duración / 30 (1 h = 2 bloques). Si no cumple, Validación muestra 'Revisar bloques' y el registro NO suma en ningún total. "
     "Nunca se redondea automáticamente. Podés cargar varias tareas por día y dividir una tarea en varios registros (mismo Task ID)."),
    ("Validaciones automáticas",
     "Se detectan: fecha fuera de período, hora faltante, inicio = fin, descanso ≥ duración, registros duplicados (misma empresa, fecha, horario y Task ID: se marca el segundo), "
     "superposición con otro registro de la MISMA empresa (ambos se marcan) y bloques inválidos. Un registro de Malcolm que se superpone con uno de proMX es válido "
     "pero se avisa en Alerta y se descuenta del 'Tiempo cronológico único'."),
    ("Cruce de medianoche",
     "Si la hora fin es menor que la de inicio (ej. 22:00–02:00) se interpreta que termina al día siguiente. Las horas se reparten en cada día en los resúmenes diarios y el calendario, "
     "y las normales/extras se evalúan según el calendario de cada día. (Los filtros de mes del Dashboard usan la fecha de inicio.)"),
    ("Horas normales y extras",
     "Normales = tiempo dentro de [Hora inicio jornada, Hora fin jornada] en días laborables de esa empresa. Todo lo demás es extra (antes de 09:00, después de 17:00, fines de semana y feriados no laborables). "
     "El descanso se descuenta según 'Regla de descanso' (Configuración). Si sabés cuándo fue el descanso, dividí la tarea en dos registros."),
    ("Cambiar un estado proMX",
     "Cambiá la columna Estado proMX del mismo registro (no lo dupliques). Al pasar a Submitted completá Fecha envío; al pasar a Approved completá Fecha aprobación. "
     "Cada registro tiene un único estado: las horas se mueven de un indicador a otro, nunca se duplican. Cancelled sale de todos los totales."),
    ("Ver horas pendientes",
     "Control proMX → 'Pendientes': lista Draft, Submitted, Returned y Recall Requested con fecha de envío y antigüedad. Pendiente de envío = Draft + Returned. "
     "Pendiente de aprobación = Submitted + Recall Requested. Se alerta (naranja) si un registro supera los días máximos en Submitted."),
    ("Consultar el dashboard",
     "Dashboard → elegí Mes y Proyecto en las celdas amarillas. Los indicadores aplican ambos filtros; los gráficos usan el filtro de proyecto. "
     "'Tiempo cronológico único' descuenta las superposiciones Malcolm/proMX."),
    ("Calendario",
     "Calendario Visual → escribí o elegí una fecha de la semana. Los bloques se colorean por empresa/estado (Malcolm azul, Draft turquesa, Submitted violeta, Approved verde, Returned rojo, "
     "naranja = superposición). Debajo está la vista mensual: elegí el mes."),
    ("Registrar un cobro",
     "Pagos → Registro de cobros: Fecha, Importe USD, Período (mes trabajado), Referencia. Podés cargar varios pagos parciales para el mismo período. "
     "Pendiente de cobro = USD aprobados − USD cobrados. Las horas Malcolm nunca entran en los cálculos económicos."),
    ("Modificar la configuración",
     "Configuración → celdas amarillas: horario, horas objetivo, bloque, tarifas, días máximos, regla de descanso, días laborables, feriados (federales y corporativos, con 'No laborable' por empresa) y proyectos."),
    ("Agregar filas / borrar ejemplos",
     f"Hay {NROWS} filas listas para usar. Para más, desproteger la hoja y copiar la última fila hacia abajo (los totales ya leen hasta la fila {LAST}). "
     "Los ejemplos tienen '[EJEMPLO]' en la descripción: borrá el contenido de las celdas amarillas de esas filas (no elimines filas) y los totales se recalculan."),
    ("Protección",
     "Las hojas están protegidas SIN contraseña para cuidar las fórmulas (Revisar → Desproteger hoja). Los filtros funcionan con la hoja protegida."),
    ("Limitaciones conocidas",
     "• Sin macros: el ID es el número de fila (cambia si se insertan/eliminan filas; no ordenar la tabla, usar filtros). "
     "• Las listas por ticket/proyecto muestran hasta 150/30 elementos. "
     "• La superposición se mide sobre el intervalo bruto (con descanso incluido). "
     "• Las tarifas son únicas: si cambian, el cambio aplica a todo el período. "
     "• El gráfico de estados usa horas (no cantidad de registros)."),
]
for i, (h, t) in enumerate(TXT):
    r = 4 + i
    a = ins.cell(r, 2, h)
    a.font = font(10, True, NAVY)
    a.alignment = Alignment(vertical="top", wrap_text=True)
    a.fill = fill(BG2)
    b = ins.cell(r, 3, t)
    b.font = font(10)
    b.alignment = Alignment(vertical="top", wrap_text=True)
    ins.row_dimensions[r].height = max(30, 15 * (len(t) // 120 + 1) + 6)
    for cc in (2, 3):
        ins.cell(r, cc).border = BORDER
protect(ins)
protect(cfg)

from openpyxl.worksheet.properties import PageSetupProperties
PRINT = {"Dashboard": "A1:Q76", "Calendario Visual": "A1:H75", "Resumen Trimestral": "A1:H45"}
for n, w in ws_.items():
    w.page_setup.orientation = "landscape"
    w.page_setup.paperSize = w.PAPERSIZE_A4
    w.sheet_properties.pageSetUpPr = PageSetupProperties(fitToPage=True)
    w.page_setup.fitToWidth = 1
    w.page_setup.fitToHeight = 0
    w.print_options.horizontalCentered = True
    w.page_margins.left = w.page_margins.right = 0.4
    if n in PRINT:
        w.print_area = PRINT[n]
reg.print_title_rows = f"{FIRST - 1}:{FIRST - 1}"

wb.calculation.fullCalcOnLoad = True
wb.active = 0
wb.save(OUT)
print("OK", OUT)
