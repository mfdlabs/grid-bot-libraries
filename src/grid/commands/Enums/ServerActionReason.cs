﻿namespace Grid.Commands;

/// <summary>
/// Reasoning for a server action.
/// </summary>
public enum ServerActionReason
{
    /// <summary>
    /// Moderation action.
    /// </summary>
    Moderation,

    /// <summary>
    /// Developer has requested the shutdown.
    /// </summary>
    Developer
}
