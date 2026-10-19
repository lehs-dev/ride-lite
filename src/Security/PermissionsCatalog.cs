namespace SwiftRide.Security;

public static class PermissionsCatalog
{
    // Trips
    public const string TripsCreate = "trips.create";
    public const string TripsRead = "trips.read";
    public const string TripsAccept = "trips.accept";
    public const string TripsUpdateStatus = "trips.update_status";
    public const string TripsCancel = "trips.cancel";
    public const string TripsAdmin = "trips.admin";

    // Matching & Pricing
    public const string MatchingQuote = "matching.quote";
    public const string MatchingMatch = "matching.match";

    // Payments
    public const string PaymentsCreate = "payments.create";
    public const string PaymentsRead = "payments.read";
    public const string PaymentsRefund = "payments.refund";
    public const string PaymentsAdmin = "payments.admin";

    // Identity & RBAC
    public const string RolesManage = "roles.manage";
    public const string UsersManage = "users.manage";
    public const string UsersRead = "users.read";

    public static readonly IReadOnlyList<string> All =
    [
        TripsCreate,
        TripsRead,
        TripsAccept,
        TripsUpdateStatus,
        TripsCancel,
        TripsAdmin,
        MatchingQuote,
        MatchingMatch,
        PaymentsCreate,
        PaymentsRead,
        PaymentsRefund,
        PaymentsAdmin,
        RolesManage,
        UsersManage,
        UsersRead
    ];
}
