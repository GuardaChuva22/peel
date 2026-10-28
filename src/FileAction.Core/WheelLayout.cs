namespace FileAction.Core;

/// <summary>Applies the user's wheel customisation: hidden slices and favourites placed first.</summary>
public static class WheelLayout
{
    public static IReadOnlyList<Operation> Arrange(IEnumerable<Operation> operations, ICollection<string> hidden, IReadOnlyList<string> favorites) =>
        operations
            .Where(o => !hidden.Contains(o.Id))
            .Select((o, i) => (Op: o, Index: i, Favorite: IndexOf(favorites, o.Id)))
            .OrderBy(x => x.Favorite < 0 ? int.MaxValue : x.Favorite)
            .ThenBy(x => x.Index)
            .Select(x => x.Op)
            .ToList();

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == id)
                return i;
        }
        return -1;
    }
}
