using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using SFA.DAS.Tools.Support.Jobs.Profiles;

namespace SFA.DAS.Tools.Support.Jobs.Search;

public sealed class UserSearchDocument
{
    [SimpleField(IsKey = true)]
    public string Id { get; set; } = "";

    [SimpleField(IsFilterable = true)]
    public string UserId { get; set; } = "";

    [SearchableField(IndexAnalyzerName = "email_index", SearchAnalyzerName = "email_search")]
    public string EmailAddress { get; set; } = "";

    [SearchableField(IsSortable = true)]
    public string DisplayName { get; set; } = "";

    public static UserSearchDocument From(UserProfile user) => new()
    {
        Id = user.Id.ToString("D"),
        UserId = user.Id.ToString("D"),
        EmailAddress = user.Email ?? "",
        DisplayName = user.DisplayName ?? ""
    };

    public static SearchIndex CreateIndex(string name)
    {
        var index = new SearchIndex(name, new FieldBuilder().Build(typeof(UserSearchDocument)));
        index.TokenFilters.Add(new EdgeNGramTokenFilter("email_prefix") { MinGram = 3, MaxGram = 255 });
        index.Analyzers.Add(new CustomAnalyzer("email_index", LexicalTokenizerName.UaxUrlEmail)
        {
            TokenFilters = { TokenFilterName.Lowercase, "email_prefix" }
        });
        index.Analyzers.Add(new CustomAnalyzer("email_search", LexicalTokenizerName.UaxUrlEmail)
        {
            TokenFilters = { TokenFilterName.Lowercase }
        });
        return index;
    }
}
