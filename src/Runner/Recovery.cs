namespace Runner;

public static class Recovery
{
    public static bool IsPending(RunRecord item) => item.RecoveryAcknowledgedAt == null && item.State is "中断" or "需人工核实" or "操作结果不明" or "操作失败或结果不明" or "异常";
    public static bool HasPending(Store store) => store.History.Any(IsPending);
    public static int PendingCount(Store store) => store.History.Count(IsPending);
    public static List<RunRecord> Pending(Store store) => store.History.Where(IsPending).ToList();
    public static bool IsSystemAction(RunRecord item) => item.State is "操作结果不明" or "操作失败或结果不明";
    public static void Acknowledge(Store store, RunRecord item)
    {
        if (!store.History.Contains(item) || !IsPending(item)) throw new InvalidOperationException("该事项已不存在或不需要核实。");
        var at=DateTimeOffset.Now;
        item.RecoveryAcknowledgedAt=at;
        item.Events.Add($"{at:O} 用户确认已人工检查；保留原状态，不自动重跑或重新请求");
        try { store.Save(); }
        catch { item.RecoveryAcknowledgedAt=null; item.Events.RemoveAt(item.Events.Count-1); throw; }
    }
}
