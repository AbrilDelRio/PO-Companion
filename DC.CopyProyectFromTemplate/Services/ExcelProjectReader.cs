using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DC.CopyProyectFromTemplate.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Services;

public sealed partial class ExcelProjectReader
{
    private static readonly CultureInfo SpanishCulture = CultureInfo.GetCultureInfo("es-ES");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    private readonly ILogger<ExcelProjectReader> logger;
    private readonly TimeZoneInfo sourceTimeZone;
    private readonly decimal hoursPerWorkingDay;

    public ExcelProjectReader(
        ILogger<ExcelProjectReader> logger,
        IConfiguration configuration)
    {
        this.logger = logger;

        string sourceTimeZoneId = configuration["ExcelSourceTimeZone"]?.Trim()
            ?? configuration["MppSourceTimeZone"]?.Trim()
            ?? TimeZoneInfo.Utc.Id;

        try
        {
            sourceTimeZone = TimeZoneInfo.FindSystemTimeZoneById(sourceTimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"ExcelSourceTimeZone '{sourceTimeZoneId}' is not a valid time-zone identifier.",
                ex);
        }

        if (!decimal.TryParse(
                configuration["ExcelHoursPerWorkingDay"],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out hoursPerWorkingDay) ||
            hoursPerWorkingDay <= 0m)
        {
            hoursPerWorkingDay = 8m;
        }
    }

    public ExcelPlanReadResult Read(string filePath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(filePath);
            string worksheetPath = ResolveTaskWorksheetPath(archive);
            IReadOnlyList<string> sharedStrings = ReadSharedStrings(archive);
            ZipArchiveEntry worksheetEntry = GetRequiredEntry(archive, worksheetPath);

            List<ExcelTaskRow> rows = ReadTaskRows(worksheetEntry, sharedStrings);
            if (rows.Count == 0)
            {
                throw new InvalidDataException(
                    "The Excel task sheet does not contain any importable rows.");
            }

            MppPlan plan = BuildPlan(rows, out int truncatedPredecessorRows);

            logger.LogInformation(
                "Read Excel plan with {TaskCount} tasks and {DependencyCount} dependencies. " +
                "Truncated predecessor rows: {TruncatedPredecessorRows}. Source time zone: {SourceTimeZone}.",
                plan.Tasks.Count,
                plan.Dependencies.Count,
                truncatedPredecessorRows,
                sourceTimeZone.Id);

            return new ExcelPlanReadResult
            {
                Plan = plan,
                TruncatedPredecessorRows = truncatedPredecessorRows
            };
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                "The uploaded file could not be read as a valid Excel project export.",
                ex);
        }
    }

    private List<ExcelTaskRow> ReadTaskRows(
        ZipArchiveEntry worksheetEntry,
        IReadOnlyList<string> sharedStrings)
    {
        List<ExcelTaskRow> rows = new();
        Dictionary<int, string>? headers = null;
        ExcelColumns? columns = null;

        using Stream stream = worksheetEntry.Open();
        using XmlReader reader = XmlReader.Create(
            stream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true
            });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.LocalName, "row", StringComparison.Ordinal))
            {
                continue;
            }

            using XmlReader rowReader = reader.ReadSubtree();
            XElement rowElement = XElement.Load(rowReader, LoadOptions.None);
            Dictionary<int, string> values = ReadRowValues(rowElement, sharedStrings);
            if (values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            int excelRowNumber = TryReadIntAttribute(rowElement, "r") ?? rows.Count + 1;

            if (headers == null)
            {
                headers = values.ToDictionary(
                    item => item.Key,
                    item => NormalizeToken(item.Value));
                columns = ResolveColumns(headers);
                continue;
            }

            ExcelColumns resolved = columns!;
            string idText = GetValue(values, resolved.Id);
            string name = GetValue(values, resolved.Name).Trim();

            if (string.IsNullOrWhiteSpace(idText) && string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ||
                id <= 0)
            {
                throw new InvalidDataException(
                    $"Excel row {excelRowNumber} contains invalid task Id '{idText}'.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Task {id}";
            }

            string outlineText = GetValue(values, resolved.OutlineLevel);
            if (!int.TryParse(
                    outlineText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int outlineLevel) ||
                outlineLevel <= 0)
            {
                throw new InvalidDataException(
                    $"Excel row {excelRowNumber}, task {id}, contains invalid outline level " +
                    $"'{outlineText}'.");
            }

            rows.Add(new ExcelTaskRow
            {
                ExcelRowNumber = excelRowNumber,
                Id = id,
                Name = name,
                IsActive = ParseBoolean(GetValue(values, resolved.Active), defaultValue: true),
                TaskMode = GetValue(values, resolved.TaskMode),
                DurationHours = ParseDurationHours(
                    GetValue(values, resolved.Duration),
                    excelRowNumber,
                    id),
                Start = ParseDate(
                    GetValue(values, resolved.Start),
                    excelRowNumber,
                    id,
                    "Start"),
                Finish = ParseDate(
                    GetValue(values, resolved.Finish),
                    excelRowNumber,
                    id,
                    "Finish"),
                Predecessors = GetValue(values, resolved.Predecessors),
                OutlineLevel = outlineLevel,
                Notes = NullIfWhiteSpace(GetValue(values, resolved.Notes))
            });
        }

        if (headers == null)
        {
            throw new InvalidDataException("The Excel task sheet does not contain a header row.");
        }

        return rows;
    }

    private MppPlan BuildPlan(
        IReadOnlyList<ExcelTaskRow> rows,
        out int truncatedPredecessorRows)
    {
        Dictionary<int, ExcelTaskRow> rowsById = new();
        Dictionary<int, int> currentTaskAtLevel = new();
        MppPlan plan = new MppPlan
        {
            HoursPerWorkingDay = hoursPerWorkingDay,
            ProjectStart = rows.Select(row => row.Start).Min(),
            ProjectFinish = rows.Select(row => row.Finish).Max()
        };

        for (int index = 0; index < rows.Count; index++)
        {
            ExcelTaskRow row = rows[index];

            if (!rowsById.TryAdd(row.Id, row))
            {
                throw new InvalidDataException(
                    $"The Excel task sheet contains duplicate task Id {row.Id}.");
            }

            int? parentId = null;
            if (row.OutlineLevel > 1)
            {
                if (!currentTaskAtLevel.TryGetValue(row.OutlineLevel - 1, out int resolvedParentId))
                {
                    throw new InvalidDataException(
                        $"Excel row {row.ExcelRowNumber}, task {row.Id}, jumps to outline level " +
                        $"{row.OutlineLevel} without a parent at level {row.OutlineLevel - 1}.");
                }

                parentId = resolvedParentId;
            }

            foreach (int level in currentTaskAtLevel.Keys
                         .Where(level => level >= row.OutlineLevel)
                         .ToList())
            {
                currentTaskAtLevel.Remove(level);
            }

            currentTaskAtLevel[row.OutlineLevel] = row.Id;

            bool isSummary = index + 1 < rows.Count &&
                             rows[index + 1].OutlineLevel > row.OutlineLevel;
            bool isMilestone = !isSummary &&
                               row.DurationHours == 0m &&
                               row.Start.HasValue &&
                               row.Finish.HasValue &&
                               row.Start.Value == row.Finish.Value;

            plan.Tasks.Add(new MppTaskDefinition
            {
                SourceId = row.Id,
                SourceUniqueId = row.Id,
                ParentSourceUniqueId = parentId,
                Name = row.Name,
                Notes = row.Notes,
                OutlineLevel = row.OutlineLevel,
                IsSummary = isSummary,
                IsMilestone = isMilestone,
                IsCritical = false,
                IsActive = row.IsActive,
                Start = row.Start,
                Finish = row.Finish,
                DurationHours = row.DurationHours,
                EffortHours = 0m,
                PercentageComplete = null
            });
        }

        truncatedPredecessorRows = 0;
        HashSet<string> dependencyKeys = new(StringComparer.Ordinal);

        foreach (ExcelTaskRow row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Predecessors))
            {
                continue;
            }

            bool rowWasTruncated = row.Predecessors.Contains("...", StringComparison.Ordinal);
            if (rowWasTruncated)
            {
                truncatedPredecessorRows++;
            }

            foreach (string rawToken in row.Predecessors.Split(
                         new[] { ',', ';' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (rawToken.Contains("...", StringComparison.Ordinal))
                {
                    continue;
                }

                MppDependencyDefinition dependency = ParseDependency(
                    rawToken,
                    row.Id,
                    row.ExcelRowNumber);

                if (!rowsById.ContainsKey(dependency.PredecessorSourceUniqueId))
                {
                    plan.SkippedExternalDependencies++;
                    continue;
                }

                string key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{dependency.PredecessorSourceUniqueId}|" +
                    $"{dependency.SuccessorSourceUniqueId}|" +
                    $"{(int)dependency.Type}|{dependency.LagHours}");

                if (dependencyKeys.Add(key))
                {
                    plan.Dependencies.Add(dependency);
                }
            }
        }

        return plan;
    }

    private MppDependencyDefinition ParseDependency(
        string token,
        int successorId,
        int excelRowNumber)
    {
        Match match = PredecessorRegex().Match(token.Trim());
        if (!match.Success ||
            !int.TryParse(
                match.Groups["id"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int predecessorId))
        {
            throw new InvalidDataException(
                $"Excel row {excelRowNumber}, task {successorId}, contains unsupported " +
                $"predecessor expression '{token}'.");
        }

        string type = match.Groups["type"].Value.ToUpperInvariant();
        string lag = match.Groups["lag"].Value;

        return new MppDependencyDefinition
        {
            PredecessorSourceUniqueId = predecessorId,
            SuccessorSourceUniqueId = successorId,
            Type = type switch
            {
                "CC" or "SS" => MppDependencyType.StartToStart,
                "FF" => MppDependencyType.FinishToFinish,
                "CF" or "SF" => MppDependencyType.StartToFinish,
                _ => MppDependencyType.FinishToStart
            },
            LagHours = ParseLagHours(lag)
        };
    }

    private decimal ParseLagHours(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        Match match = NumberAndUnitRegex().Match(value.Trim());
        if (!match.Success)
        {
            return 0m;
        }

        decimal number = ParseFlexibleDecimal(match.Groups["number"].Value);
        string unit = NormalizeToken(match.Groups["unit"].Value);

        if (unit.StartsWith("min", StringComparison.Ordinal))
        {
            return number / 60m;
        }

        if (unit.StartsWith("d", StringComparison.Ordinal) ||
            unit.StartsWith("dia", StringComparison.Ordinal) ||
            unit.StartsWith("day", StringComparison.Ordinal))
        {
            return number * hoursPerWorkingDay;
        }

        if (unit.StartsWith("sem", StringComparison.Ordinal) ||
            unit.StartsWith("w", StringComparison.Ordinal))
        {
            return number * hoursPerWorkingDay * 5m;
        }

        return number;
    }

    private decimal ParseDurationHours(string value, int excelRowNumber, int taskId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        Match match = NumberAndUnitRegex().Match(value.Trim());
        if (!match.Success)
        {
            throw new InvalidDataException(
                $"Excel row {excelRowNumber}, task {taskId}, contains unsupported duration " +
                $"'{value}'.");
        }

        decimal number = Math.Max(0m, ParseFlexibleDecimal(match.Groups["number"].Value));
        string unit = NormalizeToken(match.Groups["unit"].Value.TrimEnd('?'));

        if (unit.StartsWith("min", StringComparison.Ordinal))
        {
            return number / 60m;
        }

        if (unit.StartsWith("d", StringComparison.Ordinal) ||
            unit.StartsWith("dia", StringComparison.Ordinal) ||
            unit.StartsWith("day", StringComparison.Ordinal))
        {
            return number * hoursPerWorkingDay;
        }

        if (unit.StartsWith("sem", StringComparison.Ordinal) ||
            unit.StartsWith("w", StringComparison.Ordinal))
        {
            return number * hoursPerWorkingDay * 5m;
        }

        return number;
    }

    private DateTime? ParseDate(
        string value,
        int excelRowNumber,
        int taskId,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();

        if (double.TryParse(
                trimmed,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double excelSerial) &&
            excelSerial > 0)
        {
            return NormalizeDate(DateTime.FromOADate(excelSerial));
        }

        DateTime parsed;
        DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces;
        if (DateTime.TryParse(trimmed, SpanishCulture, styles, out parsed) ||
            DateTime.TryParse(trimmed, EnglishCulture, styles, out parsed) ||
            DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, styles, out parsed) ||
            TryParseMonthFirstDate(trimmed, out parsed))
        {
            return NormalizeDate(parsed);
        }

        throw new InvalidDataException(
            $"Excel row {excelRowNumber}, task {taskId}, contains unsupported {fieldName} " +
            $"date '{value}'.");
    }

    private static bool TryParseMonthFirstDate(string value, out DateTime result)
    {
        result = default;
        Match match = MonthFirstDateRegex().Match(value);
        if (!match.Success)
        {
            return false;
        }

        Dictionary<string, int> months = new(StringComparer.OrdinalIgnoreCase)
        {
            ["enero"] = 1,
            ["january"] = 1,
            ["febrero"] = 2,
            ["february"] = 2,
            ["marzo"] = 3,
            ["march"] = 3,
            ["abril"] = 4,
            ["april"] = 4,
            ["mayo"] = 5,
            ["may"] = 5,
            ["junio"] = 6,
            ["june"] = 6,
            ["julio"] = 7,
            ["july"] = 7,
            ["agosto"] = 8,
            ["august"] = 8,
            ["septiembre"] = 9,
            ["setiembre"] = 9,
            ["september"] = 9,
            ["octubre"] = 10,
            ["october"] = 10,
            ["noviembre"] = 11,
            ["november"] = 11,
            ["diciembre"] = 12,
            ["december"] = 12
        };

        if (!months.TryGetValue(NormalizeToken(match.Groups["month"].Value), out int month) ||
            !int.TryParse(match.Groups["day"].Value, out int day) ||
            !int.TryParse(match.Groups["year"].Value, out int year) ||
            !int.TryParse(match.Groups["hour"].Value, out int hour) ||
            !int.TryParse(match.Groups["minute"].Value, out int minute))
        {
            return false;
        }

        string amPm = match.Groups["ampm"].Value.ToUpperInvariant();
        if (amPm == "PM" && hour < 12)
        {
            hour += 12;
        }
        else if (amPm == "AM" && hour == 12)
        {
            hour = 0;
        }

        try
        {
            result = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private DateTime NormalizeDate(DateTime value)
    {
        DateTime sourceValue = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (sourceTimeZone.IsInvalidTime(sourceValue))
        {
            throw new InvalidDataException(
                $"Excel date {sourceValue:yyyy-MM-dd HH:mm:ss} falls in an invalid " +
                $"daylight-saving time for zone '{sourceTimeZone.Id}'.");
        }

        return TimeZoneInfo.ConvertTimeToUtc(sourceValue, sourceTimeZone);
    }

    private static ExcelColumns ResolveColumns(IReadOnlyDictionary<int, string> headers)
    {
        return new ExcelColumns(
            FindRequiredColumn(headers, "Id", "Task Id", "Identificador"),
            FindOptionalColumn(headers, "Activo", "Active"),
            FindOptionalColumn(headers, "Modo de tarea", "Task Mode"),
            FindRequiredColumn(headers, "Nombre", "Nombre de tarea", "Task Name", "Name"),
            FindRequiredColumn(headers, "Duración", "Duracion", "Duration"),
            FindRequiredColumn(headers, "Comienzo", "Inicio", "Start"),
            FindRequiredColumn(headers, "Fin", "Finish", "End"),
            FindOptionalColumn(headers, "Predecesoras", "Predecessors"),
            FindRequiredColumn(headers, "Nivel de esquema", "Outline Level"),
            FindOptionalColumn(headers, "Notas", "Notes"));
    }

    private static int FindRequiredColumn(
        IReadOnlyDictionary<int, string> headers,
        params string[] aliases)
    {
        int? column = FindColumn(headers, aliases);
        if (!column.HasValue)
        {
            throw new InvalidDataException(
                $"The Excel task sheet is missing required column '{aliases[0]}'.");
        }

        return column.Value;
    }

    private static int? FindOptionalColumn(
        IReadOnlyDictionary<int, string> headers,
        params string[] aliases)
    {
        return FindColumn(headers, aliases);
    }

    private static int? FindColumn(
        IReadOnlyDictionary<int, string> headers,
        IEnumerable<string> aliases)
    {
        HashSet<string> normalizedAliases = aliases
            .Select(NormalizeToken)
            .ToHashSet(StringComparer.Ordinal);

        return headers
            .Where(item => normalizedAliases.Contains(item.Value))
            .Select(item => (int?)item.Key)
            .FirstOrDefault();
    }

    private static Dictionary<int, string> ReadRowValues(
        XElement row,
        IReadOnlyList<string> sharedStrings)
    {
        Dictionary<int, string> values = new();

        foreach (XElement cell in row.Elements().Where(
                     element => string.Equals(
                         element.Name.LocalName,
                         "c",
                         StringComparison.Ordinal)))
        {
            string reference = cell.Attribute("r")?.Value ?? string.Empty;
            int columnIndex = GetColumnIndex(reference);
            if (columnIndex < 0)
            {
                continue;
            }

            string type = cell.Attribute("t")?.Value ?? string.Empty;
            string value;

            if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
            {
                value = string.Concat(cell.Descendants().Where(
                    element => string.Equals(
                        element.Name.LocalName,
                        "t",
                        StringComparison.Ordinal)).Select(element => element.Value));
            }
            else
            {
                value = cell.Descendants().FirstOrDefault(
                    element => string.Equals(
                        element.Name.LocalName,
                        "v",
                        StringComparison.Ordinal))?.Value ?? string.Empty;

                if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, out int sharedStringIndex) &&
                    sharedStringIndex >= 0 &&
                    sharedStringIndex < sharedStrings.Count)
                {
                    value = sharedStrings[sharedStringIndex];
                }
                else if (string.Equals(type, "b", StringComparison.OrdinalIgnoreCase))
                {
                    value = value == "1" ? "true" : "false";
                }
            }

            values[columnIndex] = value;
        }

        return values;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry == null)
        {
            return Array.Empty<string>();
        }

        using Stream stream = entry.Open();
        XDocument document = XDocument.Load(stream, LoadOptions.None);
        return document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "si", StringComparison.Ordinal))
            .Select(item => string.Concat(item.Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "t", StringComparison.Ordinal))
                .Select(element => element.Value)))
            .ToList();
    }

    private static string ResolveTaskWorksheetPath(ZipArchive archive)
    {
        XDocument workbook;
        XDocument relationships;

        using (Stream stream = GetRequiredEntry(archive, "xl/workbook.xml").Open())
        {
            workbook = XDocument.Load(stream, LoadOptions.None);
        }

        using (Stream stream = GetRequiredEntry(
                   archive,
                   "xl/_rels/workbook.xml.rels").Open())
        {
            relationships = XDocument.Load(stream, LoadOptions.None);
        }

        XElement? selectedSheet = workbook.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "sheet", StringComparison.Ordinal))
            .FirstOrDefault(element =>
            {
                string name = NormalizeToken(element.Attribute("name")?.Value ?? string.Empty);
                return name is "tabla tareas" or "tasks" or "task table";
            })
            ?? workbook.Descendants().FirstOrDefault(
                element => string.Equals(element.Name.LocalName, "sheet", StringComparison.Ordinal));

        if (selectedSheet == null)
        {
            throw new InvalidDataException("The Excel workbook does not contain any worksheets.");
        }

        string? relationshipId = selectedSheet.Attributes()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Name.LocalName,
                "id",
                StringComparison.Ordinal))?.Value;

        XElement? relationship = relationships.Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "Relationship", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attribute("Id")?.Value,
                    relationshipId,
                    StringComparison.Ordinal));

        string? target = relationship?.Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidDataException("The Excel task worksheet relationship is invalid.");
        }

        return NormalizeArchivePath("xl", target);
    }

    private static string NormalizeArchivePath(string basePath, string target)
    {
        string path = target.Replace('\\', '/');
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = $"{basePath.TrimEnd('/')}/{path}";
        }

        Stack<string> parts = new();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.Pop();
                }

                continue;
            }

            parts.Push(part);
        }

        return string.Join('/', parts.Reverse());
    }

    private static ZipArchiveEntry GetRequiredEntry(ZipArchive archive, string path)
    {
        return archive.GetEntry(path)
            ?? throw new InvalidDataException(
                $"The Excel workbook is missing required package entry '{path}'.");
    }

    private static int GetColumnIndex(string cellReference)
    {
        int index = 0;
        int letters = 0;

        foreach (char character in cellReference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            index = checked(index * 26 + char.ToUpperInvariant(character) - 'A' + 1);
            letters++;
        }

        return letters == 0 ? -1 : index - 1;
    }

    private static int? TryReadIntAttribute(XElement element, string name)
    {
        return int.TryParse(element.Attribute(name)?.Value, out int value)
            ? value
            : null;
    }

    private static string GetValue(IReadOnlyDictionary<int, string> values, int? column)
    {
        return column.HasValue && values.TryGetValue(column.Value, out string? value)
            ? value
            : string.Empty;
    }

    private static bool ParseBoolean(string value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return NormalizeToken(value) switch
        {
            "si" or "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => defaultValue
        };
    }

    private static decimal ParseFlexibleDecimal(string value)
    {
        string normalized = value.Replace(" ", string.Empty).Trim();
        if (normalized.Contains(',') && !normalized.Contains('.'))
        {
            normalized = normalized.Replace(',', '.');
        }
        else
        {
            normalized = normalized.Replace(",", string.Empty);
        }

        return decimal.Parse(
            normalized,
            NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture);
    }

    private static string NormalizeToken(string value)
    {
        string decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        StringBuilder builder = new StringBuilder(decomposed.Length);

        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : ' ');
        }

        return string.Join(
            ' ',
            builder.ToString().Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    [GeneratedRegex(
        @"^(?<id>\d+)\s*(?<type>CC|FC|FF|CF|SS|FS|SF)?\s*(?<lag>[+-]\s*[\d.,]+\s*[\p{L}%?]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PredecessorRegex();

    [GeneratedRegex(
        @"(?<number>[+-]?\s*\d+(?:[.,]\d+)?)\s*(?<unit>[\p{L}%?]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumberAndUnitRegex();

    [GeneratedRegex(
        @"^(?<month>[\p{L}.]+)\s+(?<day>\d{1,2})\s+(?<year>\d{4})\s+" +
        @"(?<hour>\d{1,2}):(?<minute>\d{2})(?:\s*(?<ampm>AM|PM))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthFirstDateRegex();

    private sealed record ExcelColumns(
        int Id,
        int? Active,
        int? TaskMode,
        int Name,
        int Duration,
        int Start,
        int Finish,
        int? Predecessors,
        int OutlineLevel,
        int? Notes);

    private sealed class ExcelTaskRow
    {
        public int ExcelRowNumber { get; init; }

        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public bool IsActive { get; init; }

        public string TaskMode { get; init; } = string.Empty;

        public decimal DurationHours { get; init; }

        public DateTime? Start { get; init; }

        public DateTime? Finish { get; init; }

        public string Predecessors { get; init; } = string.Empty;

        public int OutlineLevel { get; init; }

        public string? Notes { get; init; }
    }
}
