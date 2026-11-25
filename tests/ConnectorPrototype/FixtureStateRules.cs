namespace ConnectorPrototype;

// These synthetic marker IDs belong only to the controlled state fixture, not MAA.
internal static class FixtureStateRules
{
    static readonly IReadOnlyDictionary<string, string> Task = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["fixture-task-init"]="INITIALIZING", ["fixture-task-idle"]="IDLE", ["fixture-task-running"]="RUNNING", ["fixture-task-ended"]="END_OBSERVED", ["fixture-task-stopped"]="STOPPED_OBSERVED" };
    static readonly IReadOnlyDictionary<string, string> Update = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["fixture-update-active"]="UPDATING_OBSERVED", ["fixture-update-idle"]="NO_UPDATE_OBSERVED" };
    static readonly IReadOnlyDictionary<string, string> Error = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["fixture-error-present"]="ERROR_OBSERVED", ["fixture-error-clear"]="NO_ERROR_OBSERVED" };
    internal static Snapshot Apply(Snapshot observation)
    {
        if (observation.Quality != Quality.Partial || observation.ControlsTruncated) return observation with { TaskState="UNKNOWN", UpdateState="UNKNOWN", ErrorState="UNKNOWN", StateEvidence=Array.Empty<StateEvidence>() };
        var all = new List<StateEvidence>();
        StateEvidence Dimension(string name, IReadOnlyDictionary<string,string> rules)
        {
            var found=observation.Controls.Where(x=>x.Type=="Button" && x.Enabled && x.SelectedId!=null && rules.ContainsKey(x.SelectedId)).ToArray();
            var q=found.Length==0 ? Quality.Unavailable : found.Length==1 ? Quality.Partial : Quality.Conflict;
            string value=found.Length==1 ? rules[found[0].SelectedId!] : "UNKNOWN";
            return new(name,value,q,"controlled-fixture/UIA-marker",observation.Session,observation.Sequence,observation.ObservedAt,observation.ObservedAt.AddSeconds(2));
        }
        all.Add(Dimension("task",Task)); all.Add(Dimension("update",Update)); all.Add(Dimension("error",Error));
        return observation with { TaskState=all[0].Value,UpdateState=all[1].Value,ErrorState=all[2].Value,
            Quality=all.Any(x=>x.Quality==Quality.Conflict)?Quality.Conflict:Quality.Partial, BusinessResult="UNKNOWN",StateEvidence=all.AsReadOnly() };
    }
    internal static Snapshot Expire(Snapshot snapshot) => snapshot with { Quality=Quality.Stale, Detail="observation expired", Controls=Array.Empty<ControlObservation>(), WindowHandle=IntPtr.Zero, TaskState="UNKNOWN", UpdateState="UNKNOWN", ErrorState="UNKNOWN", StateEvidence=Array.Empty<StateEvidence>() };
}
