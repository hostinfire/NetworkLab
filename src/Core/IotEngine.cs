namespace SiscoNet.Core;

public sealed record IotActionResult(Guid SensorDeviceId, Guid TargetDeviceId, string Detail, bool Applied);

public static class IotEngine
{
    public static IReadOnlyList<IotActionResult> SetSensorValue(NetworkProject project, Guid sensorId, string property, double value)
    {
        var sensor = project.Devices.FirstOrDefault(device => device.Id == sensorId);
        if (sensor is null) return [];
        sensor.SensorValues[property] = value;
        var results = new List<IotActionResult>();
        foreach (var rule in project.IotRules.Where(rule => rule.Enabled && rule.SensorDeviceId == sensorId && rule.Property.Equals(property, StringComparison.OrdinalIgnoreCase)))
        {
            var triggered = Compare(value, rule.Comparison, rule.Threshold);
            rule.LastTriggered = triggered;
            if (!triggered) continue;
            rule.LastTriggeredAt = DateTime.UtcNow;
            var target = project.Devices.FirstOrDefault(device => device.Id == rule.TargetDeviceId);
            if (target is null)
            {
                results.Add(new IotActionResult(sensor.Id, rule.TargetDeviceId, "Automation target no longer exists.", false));
                continue;
            }
            target.ActuatorEnabled = rule.Action.Equals("on", StringComparison.OrdinalIgnoreCase) || rule.Action.Equals("open", StringComparison.OrdinalIgnoreCase);
            results.Add(new IotActionResult(sensor.Id, target.Id, $"{sensor.Name}.{property}={value} {rule.Comparison} {rule.Threshold}; {target.Name} set to {rule.Action}.", true));
        }
        return results;
    }

    public static bool SetActuator(DeviceModel device, bool enabled)
    {
        if (!IsActuator(device.Kind)) return false;
        device.ActuatorEnabled = enabled;
        return true;
    }

    public static bool IsActuator(string kind) => kind is "Smart Light" or "Fan" or "Door" or "Smart Appliance";

    private static bool Compare(double value, string comparison, double threshold) => comparison switch
    {
        ">" => value > threshold,
        ">=" => value >= threshold,
        "<" => value < threshold,
        "<=" => value <= threshold,
        "==" => Math.Abs(value - threshold) < 0.000001,
        "!=" => Math.Abs(value - threshold) >= 0.000001,
        _ => false
    };
}