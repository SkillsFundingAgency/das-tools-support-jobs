using System.Globalization;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using SFA.DAS.Tools.Support.Jobs.Accounts;

namespace SFA.DAS.Tools.Support.Jobs.Search;

public sealed class AccountSearchDocument
{
    [SimpleField(IsKey = true)]
    public string Id { get; set; } = "";

    [SimpleField(IsFilterable = true)]
    public long AccountId { get; set; }

    [SearchableField(IsFilterable = true, IndexAnalyzerName = "account_reference_index", SearchAnalyzerName = "account_reference_search")]
    public string PublicHashedId { get; set; } = "";

    [SearchableField(IsSortable = true)]
    public string AccountName { get; set; } = "";

    [SearchableField(IsFilterable = true, IndexAnalyzerName = "account_reference_index", SearchAnalyzerName = "account_reference_search")]
    public string[] PayeSchemes { get; set; } = [];

    [SearchableField]
    public string[] LegalEntityNames { get; set; } = [];

    public static AccountSearchDocument From(AccountDetail account, IEnumerable<string> legalEntityNames) => new()
    {
        Id = account.AccountId.ToString(CultureInfo.InvariantCulture),
        AccountId = account.AccountId,
        PublicHashedId = account.PublicHashedAccountId ?? "",
        AccountName = account.DasAccountName ?? "",
        PayeSchemes = account.PayeSchemes!.Select(paye => paye.Id!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        LegalEntityNames = legalEntityNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
    };

    public static SearchIndex CreateIndex(string name)
    {
        var index = new SearchIndex(name, new FieldBuilder().Build(typeof(AccountSearchDocument)));
        index.TokenFilters.Add(new EdgeNGramTokenFilter("account_reference_prefix") { MinGram = 3, MaxGram = 255 });
        index.Analyzers.Add(new CustomAnalyzer("account_reference_index", LexicalTokenizerName.Keyword)
        {
            TokenFilters = { TokenFilterName.Lowercase, "account_reference_prefix" }
        });
        index.Analyzers.Add(new CustomAnalyzer("account_reference_search", LexicalTokenizerName.Keyword)
        {
            TokenFilters = { TokenFilterName.Lowercase }
        });
        return index;
    }
}
