namespace DC.CopyProyectFromTemplate.Services;

/// <summary>
/// Picks the columns a query asks for among the ones the environment actually has. Project
/// Operations and older Project Service environments do not share one schema: the project finish,
/// for example, is msdyn_finish in the first and msdyn_scheduledend in the second, and asking
/// Dataverse for a column the table lacks fails the whole request.
/// </summary>
public static class DataverseColumns
{
    /// <summary>The first candidate the table has, or null when it has none.</summary>
    public static string? FirstPresent(Func<string, bool> exists, params string[] candidates)
    {
        return candidates.FirstOrDefault(exists);
    }

    /// <summary>The candidates the table has, in the order given.</summary>
    public static string[] Present(Func<string, bool> exists, params string?[] candidates)
    {
        return candidates.OfType<string>().Where(exists).ToArray();
    }
}
