using UnityEngine;

/// <summary>
/// Shared crew role for local co-op (Pilot flies, Gunner shoots + shields)
/// and online co-op (Mirror pairing). Solo keeps the legacy single-player
/// behaviour where one player does everything.
/// </summary>
public enum CrewRole
{
    Solo = 0,
    Pilot = 1,
    Gunner = 2,
}
