namespace SiscoNet.Core;

public sealed record ObjectiveResult(Guid ObjectiveId, string Title, bool Complete, int PointsEarned, int PointsAvailable);
public sealed record LabEvaluationResult(int Score, int MaximumScore, IReadOnlyList<ObjectiveResult> Objectives);

public static class LabEvaluationEngine
{
    public static LabEvaluationResult Evaluate(NetworkProject project)
    {
        var results = project.Objectives.Select(objective =>
        {
            var device = project.Devices.FirstOrDefault(candidate => candidate.Name.Equals(objective.DeviceName, StringComparison.OrdinalIgnoreCase));
            var iface = device?.Interfaces.FirstOrDefault(candidate => candidate.Name.Equals(objective.InterfaceName, StringComparison.OrdinalIgnoreCase));
            var complete = device is not null && iface is not null &&
                (string.IsNullOrWhiteSpace(objective.ExpectedIpv4Address) || iface.Ipv4Address == objective.ExpectedIpv4Address) &&
                (string.IsNullOrWhiteSpace(objective.ExpectedSubnetMask) || iface.SubnetMask == objective.ExpectedSubnetMask) &&
                (string.IsNullOrWhiteSpace(objective.ExpectedGateway) || device.DefaultGateway == objective.ExpectedGateway);
            return new ObjectiveResult(objective.Id, objective.Title, complete, complete ? Math.Max(0, objective.Points) : 0, Math.Max(0, objective.Points));
        }).ToArray();
        return new LabEvaluationResult(results.Sum(result => result.PointsEarned), results.Sum(result => result.PointsAvailable), results);
    }

    public static string GetHint(NetworkProject project, Guid objectiveId) =>
        project.Objectives.FirstOrDefault(objective => objective.Id == objectiveId)?.Hint ?? "No hint is configured for this objective.";
}