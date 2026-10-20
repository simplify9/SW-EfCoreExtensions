using SW.PrimitiveTypes;

namespace SW.EfCoreExtensions
{
    internal static class SearchyFilterExtensions
    {
        public static bool IsValid(this SearchyFilter searchyFilter)
        {
            return !string.IsNullOrWhiteSpace(searchyFilter.Field) && searchyFilter.Rule != default;
        }
    }
}
