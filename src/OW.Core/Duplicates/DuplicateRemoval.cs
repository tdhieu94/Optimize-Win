using OW.Core.Execution;

namespace OW.Core.Duplicates;

/// <summary>What the user chose to remove, after every safety check, ready for <see cref="SafeExecutor"/>.</summary>
public sealed record DuplicateRemovalPlan(IReadOnlyList<QuarantineFileAction> Actions, int Skipped, string? Error);

/// <summary>
/// Turns the copies the user ticked into a quarantine plan. Rules:
/// <list type="bullet">
/// <item>Only paths that the scan reported can be removed.</item>
/// <item>Every group keeps at least one copy; a request that would remove all copies of a file is refused whole.</item>
/// <item>Right before the plan is built, each removed copy and at least one kept copy are hashed again:
/// if a file changed or the kept copy is gone, nothing in that group is touched.</item>
/// </list>
/// </summary>
public static class DuplicateRemoval
{
    public static DuplicateRemovalPlan Plan(IReadOnlyList<DuplicateGroup> groups, IReadOnlyCollection<string> selectedPaths, Func<string, string?> hashNow)
    {
        var selected = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            return new DuplicateRemovalPlan([], 0, "Chưa chọn file nào để bỏ.");
        }

        var known = groups.SelectMany(g => g.Files).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Any(p => !known.Contains(p)))
        {
            return new DuplicateRemovalPlan([], 0, "Có file không nằm trong kết quả quét. Hãy quét lại rồi chọn.");
        }

        var touched = groups.Where(g => g.Files.Any(f => selected.Contains(f.Path))).ToList();
        if (touched.Any(g => g.Files.All(f => selected.Contains(f.Path))))
        {
            return new DuplicateRemovalPlan([], 0, "Mỗi nhóm phải giữ lại ít nhất 1 bản. Hãy bỏ chọn một bản trong nhóm đã chọn hết.");
        }

        var actions = new List<QuarantineFileAction>();
        var skipped = 0;
        foreach (var g in touched)
        {
            var remove = g.Files.Where(f => selected.Contains(f.Path)).ToList();
            var keep = g.Files.Where(f => !selected.Contains(f.Path)).ToList();

            // The copy we keep must still be there with the same content, or removing the others could lose the file.
            if (!keep.Any(k => hashNow(k.Path) == g.Sha256))
            {
                skipped += remove.Count;
                continue;
            }

            foreach (var f in remove)
            {
                if (hashNow(f.Path) == g.Sha256)
                {
                    actions.Add(new QuarantineFileAction(f.Path, f.SizeBytes, f.LastWriteUtc));
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new DuplicateRemovalPlan(actions, skipped, null);
    }
}
