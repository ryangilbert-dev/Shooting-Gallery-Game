namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// The overall state of a single match, driven server-side by <see cref="MatchManager"/>.
    /// </summary>
    public enum GamePhase
    {
        WaitingForPlayers,
        GalleryPhase,
        WallDropping,
        DuelPhase,
        RoundResolution,
        MatchOver
    }

    /// <summary>Which mirrored gallery lane a connected client has been assigned to.</summary>
    public enum LaneSide
    {
        None,
        A,
        B
    }
}
