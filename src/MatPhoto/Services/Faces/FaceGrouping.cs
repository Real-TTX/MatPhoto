using System.Numerics;

namespace MatPhoto.Services.Faces;

/// <summary>A face as the grouping sees it: its person (if any), its features, and whether a person decided about it (then it stays as it is).</summary>
public sealed record GroupFace(long Id, long? PersonId, float[] Embedding, bool Confirmed = false, bool Ignored = false);

/// <summary>
/// Groups faces into people, a few new faces at a time (the faces of the photos looked at since the last time), like a person would:
/// <list type="number">
/// <item>a new face goes to the person of the most alike face that has one (a cosine of <see cref="SamePerson"/> or more) – unless a person said it is not them;</item>
/// <item>else, when at least <see cref="MinFaces"/> faces without a person are alike (<see cref="Together"/>), they become a new person without a name ("who is this?");</item>
/// <item>else the face waits: a later face may bring it into a person.</item>
/// </list>
/// The numbers suit SFace (OpenCV says 0.363 is the same person); a person is only made of faces that are clearly alike, so one person may become two groups – a name
/// joins them again – but two people rarely become one.
/// </summary>
public static class FaceGrouping
{
    /// <summary>A face this alike to a face of a person is that person.</summary>
    public const float SamePerson = 0.42f;

    /// <summary>Faces without a person this alike form a new person.</summary>
    public const float Together = 0.47f;

    /// <summary>A new person needs this many faces.</summary>
    public const int MinFaces = 3;

    /// <summary>
    /// The people of the faces <paramref name="pending"/> (and of the faces without a person they bring along): face → an existing person (a positive id) or a new
    /// one (a negative number, the same for the faces of one new person). Faces that stay without a person are not in the answer.
    /// </summary>
    public static Dictionary<long, long> Group(IReadOnlyList<GroupFace> all, IEnumerable<long> pending, IReadOnlySet<(long FaceId, long PersonId)> rejected)
    {
        var index = new Dictionary<long, int>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            index[all[i].Id] = i;
        }

        var person = all.Select(f => f.PersonId).ToArray();
        var changes = new Dictionary<long, long>();
        var next = -1L;
        foreach (var id in pending.Order())
        {
            if (!index.TryGetValue(id, out var p) || all[p].Ignored || all[p].Confirmed || person[p] is not null)
            {
                continue;
            }

            var embedding = all[p].Embedding;
            var bestPerson = (long?)null;
            var bestSimilarity = SamePerson;
            var alike = new List<int>();
            for (var i = 0; i < all.Count; i++)
            {
                if (i == p || all[i].Ignored)
                {
                    continue;
                }

                var similarity = Dot(embedding, all[i].Embedding);
                if (person[i] is { } other)
                {
                    if (similarity >= bestSimilarity && !rejected.Contains((id, other)))
                    {
                        bestSimilarity = similarity;
                        bestPerson = other;
                    }
                }
                else if (similarity >= Together && !all[i].Confirmed)
                {
                    alike.Add(i);
                }
            }

            if (bestPerson is { } found)
            {
                person[p] = found;
                changes[id] = found;
                continue;
            }

            if (alike.Count + 1 >= MinFaces)
            {
                var created = next--;
                person[p] = created;
                changes[id] = created;
                foreach (var i in alike)
                {
                    person[i] = created;
                    changes[all[i].Id] = created;
                }
            }
        }

        return changes;
    }

    /// <summary>The dot product of two vectors of length 1: their cosine.</summary>
    public static float Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
        {
            sum += new Vector<float>(a, i) * new Vector<float>(b, i);
        }

        var result = Vector.Dot(sum, Vector<float>.One);
        for (; i < n; i++)
        {
            result += a[i] * b[i];
        }

        return result;
    }
}
