using System.Collections.Generic;

namespace Content.Server.AiAgent.Skills;

/// <summary>
/// One library entry, in the shape the debug bus sees it.
///
/// <para>
/// This used to be the storage unit: <c>SkillStore</c> held such entries in a flat dictionary and
/// printed an index from them into the system prompt. Storage moved to
/// <see cref="Vfs.DocTree"/> — with nesting, permissions and search — while the type stayed, because
/// the <c>skill.updated</c> and <c>skills.reloaded</c> event format and the debugger tab hang off it.
/// Changing the wire format together with the storage would mean fixing two things at once and not
/// knowing which one broke.
/// </para>
/// <para>
/// <see cref="Name"/> is now a path inside the mount ("power/mixes"), not a flat name.
/// </para>
/// </summary>
public sealed record Skill(string Name, string When, string Body);

/// <summary>Result of editing the library from outside the agent — from the debugger's HTTP endpoint.</summary>
public sealed record SkillResult(bool Ok, string Message, IReadOnlyList<string>? Names = null);
