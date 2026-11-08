namespace PublicTransit.Routes
{
    /// <summary>Small explicit state machine for a single route.</summary>
    public enum TransitRunState
    {
        Stopped,
        WaitingForDriver,
        Dwelling,
        Moving,
        Blocked,
        Recovering,
        WaitingForEngine,
        DriverBreak,
        // Append to preserve numeric values of states in existing saves.
        PendingGoto
    }
}
