namespace PowerTools;

public sealed record SafeChangeSelection(string ObjectType, string TableName, string ObjectName);

public sealed record SafeChangePlanRequest(string ProjectPath, IReadOnlyList<SafeChangeSelection> Operations);

public sealed record ApplySafeChangeRequest(string PlanId, string ConfirmationPhrase);

public sealed record RollbackSafeChangeRequest(string PlanId, string ConfirmationPhrase);

public sealed record SafeChangeOperation(
    string OperationId,
    string Action,
    string ObjectType,
    string TableName,
    string ObjectName,
    string SourceFile,
    int RiskScore,
    string Confidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Evidence,
    string Preview);

public sealed record SafeChangeAuditEvent(
    DateTimeOffset At,
    string EventType,
    string Detail);

public sealed record SafeChangePlan(
    string PlanId,
    string ProjectName,
    string SourcePath,
    string SourceFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Status,
    string ConfirmationPhrase,
    string RollbackPhrase,
    IReadOnlyList<SafeChangeOperation> Operations,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SafeChangeAuditEvent> AuditTrail,
    string? WorkspacePath = null,
    string? AuditPath = null);
