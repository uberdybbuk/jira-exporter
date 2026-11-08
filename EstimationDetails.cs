using System.Globalization;

namespace FourArc.JiraExporter;

// One person's estimate on one request, in man-days.
public sealed record PersonEstimate(string Person, int ManDays);

// The Estimation Details field: one "Person : man-days" line per estimate, e.g.
//   Abdulkerim Demirbaş : 10
//   Zeynep Yaman-Intertech-Müşteri Proje Yönetimi Bölüm Müdürü : 0
// The person is kept as written. A person on several lines is summed, a zero estimate is left out.
// A line that does not read as "name : whole number" is returned as a problem, not dropped silently.
public sealed class EstimationDetails
{
    public List<PersonEstimate> Estimates { get; } = [];
    public List<string> Problems { get; } = [];

    public int Total => Estimates.Sum(e => e.ManDays);

    public static EstimationDetails Parse(string text)
    {
        var result = new EstimationDetails();
        Dictionary<string, int> byPerson = new(StringComparer.Ordinal);

        foreach (var rawLine in (text ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // The last colon: the number never holds one, a role name might.
            var colon = line.LastIndexOf(':');
            var person = colon > 0 ? line[..colon].Trim() : "";
            var number = colon > 0 ? line[(colon + 1)..].Trim() : "";
            if (person.Length == 0
                || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var manDays))
            {
                result.Problems.Add(line);
                continue;
            }

            if (manDays == 0)
            {
                continue;
            }

            byPerson[person] = byPerson.GetValueOrDefault(person) + manDays;
        }

        // Kept in the order the field lists them.
        result.Estimates.AddRange(byPerson.Select(p => new PersonEstimate(p.Key, p.Value)));
        return result;
    }
}
