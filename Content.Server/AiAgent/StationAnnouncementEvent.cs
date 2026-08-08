namespace Content.Server.AiAgent;

/// <summary>
/// An announcement went out over the station. Raised by <c>ChatSystem</c> for all three dispatch
/// paths — global, filtered and station-scoped.
///
/// <b>Why this exists at all.</b> Announcements are delivered by writing a chat packet straight to
/// player <em>sessions</em> (<c>ChatManager.ChatMessageToAll</c>), and neither the chat system nor
/// the chat manager raises a single event anywhere along that route. An entity without a session —
/// which is exactly what the agent's brain is — is therefore not a recipient of anything, and has
/// no way to learn that Central Command just told the station something. On a server whose whole
/// premise is that the AI listens, that showed up as the AI ignoring the round's biggest events:
/// the shuttle being called, a code change, an evacuation notice.
///
/// <b>Why it is declared here.</b> This is the fork's file, in the fork's directory, so the edit to
/// upstream is one <c>using</c> and one line per dispatch method — three additions and no changed
/// lines, which is the shape of diff that survives a rebase without conflicting.
/// </summary>
/// <param name="Sender">
/// The announcer as the crew sees it: "Central Command", a console's configured title, an admin's
/// chosen name. Already localised, because it is the same string that goes on screen.
/// </param>
/// <param name="Message">The announcement body, unwrapped and unescaped.</param>
/// <param name="Source">
/// The entity the announcement came from, when there is one — a communications console, usually.
/// Null for a global announcement, which by definition has no origin on the map and is heard
/// everywhere.
/// </param>
public readonly record struct StationAnnouncementEvent(string Sender, string Message, EntityUid? Source);
