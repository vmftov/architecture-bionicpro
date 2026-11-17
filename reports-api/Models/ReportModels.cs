namespace ReportsApi.Models;

public class Report {
    public DateTimeOffset Date { get; set; }

    public List<ReportProsthesis> Prostheses { get; set; } = new List<ReportProsthesis>();
}

public class ReportProsthesis {
    public int Id { get; set; }

    public string? Type { get; set; }

    public string? Description { get; set; }

    public List<ReportProsthesisStat> Stats { get; set; } = new List<ReportProsthesisStat>();
}

public class ReportProsthesisStat {
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Value { get; set; }
}