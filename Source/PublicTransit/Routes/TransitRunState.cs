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
        WaitingForEngine
    }
}
