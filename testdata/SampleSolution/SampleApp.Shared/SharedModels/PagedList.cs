namespace SampleApp.Shared.SharedModels;

/// <summary>A page of results from a paginated query.</summary>
public class PagedList<T> where T : class
{
    public IReadOnlyList<T> Items { get; }
    public int TotalCount { get; }
    public int PageNumber { get; }
    public int PageSize { get; }

    public bool HasNextPage => PageNumber * PageSize < TotalCount;

    public PagedList(IReadOnlyList<T> items, int totalCount, int pageNumber, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        PageNumber = pageNumber;
        PageSize = pageSize;
    }
}
