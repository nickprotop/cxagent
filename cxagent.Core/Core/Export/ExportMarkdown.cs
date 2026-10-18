using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Helpers;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Export;

/// <summary>What an export wrote, counted — the dialog's size line.</summary>
/// <param name="Messages">Messages written, sub-agents' included.</param>
/// <param name="SubAgents">Distinct sub-agents whose conversation was written.</param>
/// <param name="Bytes">The markdown's size in UTF-8.</param>
public sealed record ExportStats(int Messages, int SubAgents, int Bytes);

/// <summary>An export's markdown and its counts.</summary>
public sealed record ExportResult(string Markdown, ExportStats Stats);

/// <summary>
/// Writes a conversation as markdown.
///
/// <para>A TIMELINE, ALWAYS (spec decision 15). Nothing is regrouped: a sub-agent's work is split into the stretches
/// it worked in, and each goes where it began — the brief after the spawn, a later message after the send that
/// carried it, a follow-up typed in its tab at the time it was typed — so the page reads as the session unfolded.</para>
///
/// <para>PURE: it reads a loaded <see cref="ExportSource"/> and its children's folders, and returns text. Where the
/// text goes is the caller's business.</para>
/// </summary>
public static partial class ExportMarkdown
{
    public static ExportResult Render(ExportSource source, ExportOptions options) => new Writer(source, options).Run();

    private sealed record Stretch(DateTimeOffset Start, string ChildId, string Name, string Type, string Label,
        IReadOnlyList<ExportEntry> Entries);

    private sealed partial class Writer(ExportSource source, ExportOptions options)
    {
        private readonly StringBuilder _md = new();
        private readonly HashSet<string> _children = new(StringComparer.Ordinal);

        // CHILDREN WHOSE CONVERSATION WAS FOUND, wherever it was: after a re-wire a later send's result is written in a
        // newer folder than the one the child lives under, and is not where to look it up again.
        private readonly HashSet<string> _found = new(StringComparer.Ordinal);
        private int _messages;
        private string? _speaker;
        private bool _inQuote;

        private bool IsAgentExport => source.Header.AgentName is not null;
        private bool Has(ExportParts part) => (options.Parts & part) != 0;

        public ExportResult Run()
        {
            var scoped = Scoped(source.Entries);
            if (Has(ExportParts.Header)) WriteHeader();
            if (Has(ExportParts.SystemAndInjected) && source.Entries.FirstOrDefault(IsSystemPrompt) is { Message: { } system })
                _md.Append("## System prompt\n\n").Append(Md.Fence(system.Content.TrimEnd())).Append("\n\n");

            var start = scoped.Count > 0 ? scoped[0].At : DateTimeOffset.MinValue;
            var stretches = Has(ExportParts.SubAgents) && !IsAgentExport
                ? Stretches(scoped).Where(s => s.Start >= start).ToList()
                : [];

            WriteTimeline(scoped, stretches, "##", inChild: false);
            EndQuote();

            var markdown = _md.ToString().TrimEnd() + "\n";
            return new ExportResult(markdown,
                new ExportStats(_messages, _children.Count, Encoding.UTF8.GetByteCount(markdown)));
        }

        // ---- scope ---------------------------------------------------------------------------------------

        /// <summary>
        /// The entries the scope covers. A TURN STARTS WHERE THE USER SPOKE — for a session; a delivered report starts
        /// none, it arrived inside one. An agent's own export counts its stretches: anything it received starts one.
        /// </summary>
        private List<ExportEntry> Scoped(IReadOnlyList<ExportEntry> entries)
        {
            var all = entries.Where(e => !IsSystemPrompt(e)).ToList();
            if (options.Scope == ExportScope.Whole) return all;

            var starts = all.Select((e, i) => (e, i)).Where(p => StartsTurn(p.e)).Select(p => p.i).ToList();
            if (starts.Count == 0) return all;
            var want = options.Scope == ExportScope.LastTurn ? 1 : Math.Max(1, options.Turns);
            return all.Skip(starts[Math.Max(0, starts.Count - want)]).ToList();
        }

        // A NUDGE STARTS NOTHING: it is cxagent talking to the model mid-turn, and a turn begun there would cut off the
        // question the turn was answering.
        private bool StartsTurn(ExportEntry e) =>
            e is { Kind: HistoryLine.Kinds.Message, Message: { Role: "user", ToolCallId: null, IsTaskList: false } }
            && e.From != Incoming.Nudge
            && (IsAgentExport || e.From is null or Incoming.You);

        private static bool IsSystemPrompt(ExportEntry e) => e is { Kind: HistoryLine.Kinds.Message, Message.Role: "system" };

        // ---- the header ----------------------------------------------------------------------------------

        private void WriteHeader()
        {
            var h = source.Header;
            var facts = new List<string>();
            if (IsAgentExport)
            {
                _md.Append("# ").Append(h.AgentName).Append(h.AgentType is { } t ? $" ({t})" : "").Append('\n');
                if (h.Title is { } parent) facts.Add($"sub-agent of “{parent}”");
                if (h.State is { } state) facts.Add(state);
            }
            else
            {
                _md.Append("# ").Append(h.Title ?? "cxagent session").Append('\n');
                facts.Add("cxagent");
            }

            if (h.Started is { } started)
                facts.Add(started.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)
                    + (h.Ended is { } ended ? "–" + ended.ToLocalTime().ToString(
                        ended.ToLocalTime().Date == started.ToLocalTime().Date ? "HH:mm" : "d MMM HH:mm",
                        CultureInfo.InvariantCulture) : ""));
            if (!string.IsNullOrEmpty(h.WorkingDir)) facts.Add(Md.CodeSpan(h.WorkingDir));
            if (!string.IsNullOrEmpty(h.Model)) facts.Add(h.Model);
            if (h.InputTokens + h.OutputTokens > 0)
                facts.Add($"{DisplayNumber.Compact(h.InputTokens)} in / {DisplayNumber.Compact(h.OutputTokens)} out");

            _md.Append(string.Join(" · ", facts)).Append("\n\n---\n\n");
        }

        // ---- the timeline --------------------------------------------------------------------------------

        private void WriteTimeline(IReadOnlyList<ExportEntry> entries, IReadOnlyList<Stretch> stretches, string hashes,
            bool inChild)
        {
            var results = entries.Where(e => e.Message?.ToolCallId is not null)
                .GroupBy(e => e.Message!.ToolCallId!)
                .ToDictionary(g => g.Key, g => g.Last());

            // A STRETCH GOES AFTER THE LAST ENTRY THAT CAME BEFORE IT: anything that began before an entry is written
            // ahead of that entry.
            var next = 0;
            foreach (var entry in entries)
            {
                while (next < stretches.Count && stretches[next].Start < entry.At) WriteStretch(stretches[next++]);
                WriteEntry(entry, results, hashes, inChild);
            }
            while (next < stretches.Count) WriteStretch(stretches[next++]);
        }

        private void WriteEntry(ExportEntry entry, IReadOnlyDictionary<string, ExportEntry> results, string hashes,
            bool inChild)
        {
            switch (entry.Kind)
            {
                case ExportEntry.Note:
                    Paragraph($"> *{entry.Message?.Content}*");
                    _speaker = null;
                    return;
                case HistoryLine.Kinds.Cleared:
                    Paragraph("> *Conversation cleared.*");
                    _speaker = null;
                    return;
                case HistoryLine.Kinds.Compacted:
                    Paragraph("> *Context compacted here.*");
                    return;
            }

            if (entry.Message is not { } m || m.Role == "system" || m.ToolCallId is not null) return;

            if (m.Role == "user")
            {
                if (entry.From == Incoming.Nudge)
                {
                    if (!Has(ExportParts.SystemAndInjected)) return;
                    Paragraph("> nudge (injected)");
                    Paragraph(Quote(m.Content));
                    return;
                }
                if (m.IsTaskList)
                {
                    if (!Has(ExportParts.SystemAndInjected)) return;
                    Paragraph("> task list (injected)");
                    Paragraph(Md.Fence(m.Content.TrimEnd(), "markdown"));
                    return;
                }
                Heading(hashes, Speaker(entry.From, inChild), entry.At);
                Paragraph(Has(ExportParts.SystemAndInjected) ? m.Content : m.Shown ?? m.Content);
                _messages++;
                return;
            }

            if (m.Role != "assistant") return;

            if (IsSummary(m))
            {
                if (Has(ExportParts.Compaction))
                    Paragraph(Quote(m.Content[Sessions.SessionCompressor.SummaryMarker.Length..].TrimEnd(']')));
                return;
            }

            if (_speaker != "cxagent") Heading(hashes, "cxagent", entry.At);
            if (Has(ExportParts.Thinking) && entry.Thinking is { Count: > 0 } thoughts)
                Paragraph($"<details><summary>Thinking</summary>\n\n{string.Join("\n\n", thoughts)}\n\n</details>");
            if (!string.IsNullOrWhiteSpace(m.Content)) Paragraph(m.Content.Trim());
            if (!string.IsNullOrWhiteSpace(m.Content) || m.ToolCalls is { Count: > 0 }) _messages++;

            foreach (var call in m.ToolCalls ?? [])
                WriteCall(call, call.Id is { } id && results.TryGetValue(id, out var r) ? r : null);
        }

        private void WriteCall(ToolCall call, ExportEntry? result)
        {
            var output = result?.Message?.Content;
            if (Has(ExportParts.ToolCalls))
            {
                if (_inQuote) _md.Append(">\n");
                _md.Append("> ").Append(CallLine(call, output)).Append('\n');
                _inQuote = true;
            }

            if (Has(ExportParts.Diffs) && EditDiff.For(call) is { } diff)
                Paragraph(Md.Fence(diff, "diff"));

            if (Has(ExportParts.ToolResults) && output is not null)
                Paragraph(Md.Fence(output.TrimEnd(), LanguageOf(call)));

            if (call.Name is Tool.Agent or Tool.AgentSend && output is not null && result is not null)
                WriteSubAgentReport(output, result);
        }

        /// <summary>
        /// What stands in for a sub-agent's conversation when it is not written: its report. Written when sub-agents are
        /// left out, or when this one's conversation was never recorded — and then saying so.
        /// </summary>
        private void WriteSubAgentReport(string envelope, ExportEntry result)
        {
            if (SubAgentEnvelope.StateOf(envelope) == SubAgentEnvelope.Running || Has(ExportParts.ToolResults)) return;

            var recorded = Has(ExportParts.SubAgents) && !IsAgentExport && SubAgentEnvelope.IdOf(envelope) is { } id
                && (_found.Contains(id) || source.Child(result, id) is not null);
            if (recorded) return;

            Paragraph(Quote(Inner(envelope)));
            if (Has(ExportParts.SubAgents)) Paragraph("*conversation not recorded*");
        }

        private void WriteStretch(Stretch stretch)
        {
            _children.Add(stretch.ChildId);
            var calls = stretch.Entries.Sum(e => e.Message?.ToolCalls?.Count ?? 0);
            Paragraph($"<details><summary>Sub-agent <b>{stretch.Name}</b> ({stretch.Type}) · {stretch.Label} · "
                + $"{calls} tool call{(calls == 1 ? "" : "s")}</summary>");

            var speaker = _speaker;
            _speaker = null;
            WriteTimeline(stretch.Entries, [], "####", inChild: true);
            EndQuote();
            _md.Append("</details>\n\n");
            _speaker = speaker == "cxagent" ? null : speaker;
        }

        // ---- sub-agents ----------------------------------------------------------------------------------

        /// <summary>
        /// Every stretch of every sub-agent these entries started or messaged, by start time. A stretch begins at each
        /// message the child received; its first is the brief.
        /// </summary>
        private List<Stretch> Stretches(IReadOnlyList<ExportEntry> entries)
        {
            var calls = entries.SelectMany(e => e.Message?.ToolCalls ?? []).Where(c => c.Id is not null)
                .GroupBy(c => c.Id!).ToDictionary(g => g.Key, g => g.First());
            var typeOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stretches = new List<Stretch>();

            foreach (var result in entries)
            {
                if (result.Message is not { ToolCallId: { } callId, Content: var envelope }
                    || !calls.TryGetValue(callId, out var call) || call.Name is not (Tool.Agent or Tool.AgentSend)
                    || SubAgentEnvelope.IdOf(envelope) is not { } childId)
                    continue;

                var name = SubAgentEnvelope.NameOf(envelope) ?? childId;
                if (call.Name == Tool.Agent)
                    typeOf.TryAdd(name, Arg(call, "type") is { Length: > 0 } t ? t : AgentTypeCatalog.DefaultTypeName);
                if (!seen.Add(childId) || source.Child(result, childId) is not { } child) continue;
                _found.Add(childId);

                var type = typeOf.GetValueOrDefault(name, AgentTypeCatalog.DefaultTypeName);
                var bounds = child.Entries.Select((e, i) => (e, i))
                    .Where(p => p.e is { Kind: HistoryLine.Kinds.Message, Message: { Role: "user", ToolCallId: null, IsTaskList: false } }
                        && p.e.From != Incoming.Nudge)
                    .Select(p => p.i).ToList();
                for (var n = 0; n < bounds.Count; n++)
                {
                    var end = n + 1 < bounds.Count ? bounds[n + 1] : child.Entries.Count;
                    var part = child.Entries.Skip(bounds[n]).Take(end - bounds[n]).ToList();
                    stretches.Add(new Stretch(part[0].At, childId, name, type, LabelOf(part[0].From, n), part));
                }
            }
            return [.. stretches.OrderBy(s => s.Start)];
        }

        private static string LabelOf(Incoming? from, int index) => from switch
        {
            Incoming.Brief => "brief",
            Incoming.You => "from you",
            Incoming.Delivery => "delivery",
            _ => index == 0 ? "brief" : $"stretch {index + 1}",
        };

        // ---- pieces --------------------------------------------------------------------------------------

        private string Speaker(Incoming? from, bool inChild) => from switch
        {
            Incoming.You => "You",
            Incoming.Brief => "Brief from cxagent",
            Incoming.Parent => "From cxagent",
            Incoming.Delivery => "Delivered",
            _ => inChild || IsAgentExport ? "From cxagent" : "You",
        };

        private void Heading(string hashes, string speaker, DateTimeOffset at)
        {
            EndQuote();
            _md.Append(hashes).Append(' ').Append(speaker).Append(" · ")
                .Append(at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)).Append("\n\n");
            _speaker = speaker == "cxagent" ? "cxagent" : speaker;
        }

        private void Paragraph(string text)
        {
            EndQuote();
            _md.Append(text).Append("\n\n");
        }

        private void EndQuote()
        {
            if (!_inQuote) return;
            _md.Append('\n');
            _inQuote = false;
        }

        /// <summary>
        /// One line for a call: its name, the argument that says what it did, and — when it failed — how. The
        /// description is <see cref="Agent.DescribeCall"/>'s, the label the transcript's tool rows show.
        /// </summary>
        private string CallLine(ToolCall call, string? output)
        {
            // A MESSAGE TO A SUB-AGENT IS NAMED BY WHO IT WENT TO AND WHAT IT SAID: the transcript's row label for it is
            // its arguments as JSON, which an export would show raw.
            var label = call.Name == Tool.AgentSend
                ? $"{Arg(call, "to")} — {Clip(Arg(call, "prompt") ?? "", 60)}"
                : Agent.DescribeCall(call, source.Header.WorkingDir);

            var outcome = output is null ? ""
                : ExitCode().Match(output) is { Success: true } exit && exit.Groups[1].Value != "0" ? $" → exit {exit.Groups[1].Value}"
                : output.StartsWith("error:", StringComparison.Ordinal) ? " → error"
                : "";
            // A PATH OR A COMMAND GOES IN THE CODE SPAN WITH THE TOOL'S NAME: escaped as prose, `test_prices.py` reads
            // `test\_prices.py` in the raw file. A label that is prose — an agent's description, a plan — stays prose.
            if (label.StartsWith(call.Name, StringComparison.Ordinal))
            {
                var argument = label[call.Name.Length..].TrimStart(' ', '·').TrimStart();
                return Md.CodeSpan(argument.Length == 0 ? call.Name : $"{call.Name} {argument}") + outcome;
            }
            return $"{Md.CodeSpan(call.Name)} {Md.Escape(label)}{outcome}";
        }

        private static string Clip(string text, int max)
        {
            var line = text.ReplaceLineEndings(" ").Trim();
            return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
        }

        [GeneratedRegex(@"^exit_code: (-?\d+)", RegexOptions.Multiline)]
        private static partial Regex ExitCode();

        private static string LanguageOf(ToolCall call) =>
            Arg(call, "path") is { } path && Path.GetExtension(path) is { Length: > 1 } ext
                ? ext[1..].ToLowerInvariant()
                : "text";

        private static string Quote(string text) =>
            string.Join("\n", text.Trim().Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

        /// <summary>A sub-agent envelope's text, without its tags.</summary>
        private static string Inner(string envelope)
        {
            var open = envelope.IndexOf('>');
            var close = envelope.LastIndexOf("</sub_agent>", StringComparison.Ordinal);
            return open >= 0 && close > open ? envelope[(open + 1)..close].Trim() : envelope.Trim();
        }

        private static bool IsSummary(ChatMessage m) =>
            m.Content.StartsWith(Sessions.SessionCompressor.SummaryMarker, StringComparison.Ordinal);

        private static string? Arg(ToolCall call, string name) =>
            call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
