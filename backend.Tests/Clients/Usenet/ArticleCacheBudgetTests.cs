using NzbWebDAV.Clients.Usenet;

namespace backend.Tests.Clients.Usenet;

public sealed class ArticleCacheBudgetTests
{
    [Fact]
    public void IsOverBudget_UsesSharedConfiguredLimit()
    {
        var budget = new ArticleCacheBudget();
        budget.Configure(maxBytes: 100);

        Assert.False(budget.IsOverBudget);

        budget.Add(60);
        budget.Add(50);

        Assert.True(budget.IsOverBudget);
        Assert.Equal(110, budget.CurrentBytes);
    }

    [Fact]
    public void Remove_DoesNotUnderflow()
    {
        var budget = new ArticleCacheBudget();
        budget.Configure(maxBytes: 100);
        budget.Add(25);

        budget.Remove(50);

        Assert.Equal(0, budget.CurrentBytes);
        Assert.False(budget.IsOverBudget);
    }
}
