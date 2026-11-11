namespace ProjectBeacon.Application.Tests.Proposals;

using System.Text.Json;
using ProjectBeacon.Application.Proposals;

public sealed class ProposalMarkdownEditApplierTests
{
    private const string Document = """
        # Project

        Intro.

        ## Security

        Old security body.

        ### Threat model

        Nested details.

        ## Operations

        Operational notes.
        """;

    [Fact]
    public void Replace_ReplacesSectionBodyAndPreservesHeadingAndNestedStructure()
    {
        using var payload = JsonDocument.Parse("""
            [
              {
                "heading": "## Security",
                "op": "replace",
                "content": "New security body.\n\n### Verification\n\nVerify every boundary."
              }
            ]
            """);

        var applied = ProposalMarkdownEditApplier.TryApply(
            Document, payload.RootElement, out var result, out var error);

        Assert.True(applied, error);
        Assert.Contains("## Security", result);
        Assert.Contains("New security body.", result);
        Assert.Contains("### Verification", result);
        Assert.DoesNotContain("Old security body.", result);
        Assert.DoesNotContain("### Threat model", result);
        Assert.Contains("## Operations", result);
    }

    [Fact]
    public void InsertAfter_InsertsAfterWholeTargetSectionAndBeforeNextSibling()
    {
        using var payload = JsonDocument.Parse("""
            [
              {
                "heading": "## Security",
                "op": "insert_after",
                "content": "## Data handling\n\nData handling rules."
              }
            ]
            """);

        var applied = ProposalMarkdownEditApplier.TryApply(
            Document, payload.RootElement, out var result, out var error);

        Assert.True(applied, error);
        Assert.True(result.IndexOf("### Threat model", StringComparison.Ordinal)
            < result.IndexOf("## Data handling", StringComparison.Ordinal));
        Assert.True(result.IndexOf("## Data handling", StringComparison.Ordinal)
            < result.IndexOf("## Operations", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_RemovesTargetSectionAndItsNestedHeadings()
    {
        using var payload = JsonDocument.Parse("""
            [
              {
                "heading": "## Security",
                "op": "delete",
                "content": ""
              }
            ]
            """);

        var applied = ProposalMarkdownEditApplier.TryApply(
            Document, payload.RootElement, out var result, out var error);

        Assert.True(applied, error);
        Assert.DoesNotContain("## Security", result);
        Assert.DoesNotContain("Old security body.", result);
        Assert.DoesNotContain("### Threat model", result);
        Assert.Contains("# Project", result);
        Assert.Contains("## Operations", result);
        Assert.Contains("Operational notes.", result);
    }

    [Fact]
    public void MissingHeading_ReturnsAvailableHeadingsInsteadOfChangingContent()
    {
        using var payload = JsonDocument.Parse("""
            [
              {
                "heading": "## Authentication",
                "op": "replace",
                "content": "New content."
              }
            ]
            """);

        var applied = ProposalMarkdownEditApplier.TryApply(
            Document, payload.RootElement, out var result, out var error);

        Assert.False(applied);
        Assert.Equal(Document, result);
        Assert.Contains("was not found", error);
        Assert.Contains("## Security", error);
        Assert.Contains("## Operations", error);
    }

    [Fact]
    public void AmbiguousHeading_IsRejectedWithAvailableHeadings()
    {
        const string duplicate = """
            ## Security

            First section.

            ## Security

            Second section.
            """;
        using var payload = JsonDocument.Parse("""
            [
              {
                "heading": "## Security",
                "op": "delete",
                "content": ""
              }
            ]
            """);

        var applied = ProposalMarkdownEditApplier.TryApply(
            duplicate, payload.RootElement, out var result, out var error);

        Assert.False(applied);
        Assert.Equal(duplicate, result);
        Assert.Contains("is ambiguous", error);
        Assert.Contains("2 matches", error);
    }

    [Fact]
    public void HeadingInsideFencedCodeBlock_IsNotAddressable()
    {
        const string code = """
            # Project

            ```markdown
            ## Security
            This is an example, not a section.
            ```
            """;
        using var payload = JsonDocument.Parse("""
            [
              {
                "heading": "## Security",
                "op": "delete",
                "content": ""
              }
            ]
            """);

        var applied = ProposalMarkdownEditApplier.TryApply(
            code, payload.RootElement, out var result, out var error);

        Assert.False(applied);
        Assert.Equal(code, result);
        Assert.Contains("was not found", error);
    }

    [Fact]
    public void LaterMissingHeading_DoesNotReturnPartiallyModifiedContent()
    {
        using var payload = JsonDocument.Parse("""
            [
              {
                "heading": "## Security",
                "op": "replace",
                "content": "Updated security."
              },
              {
                "heading": "## Missing",
                "op": "delete",
                "content": ""
              }
            ]
            """);

        var applied = ProposalMarkdownEditApplier.TryApply(
            Document, payload.RootElement, out var result, out var error);

        Assert.False(applied);
        Assert.Equal(Document, result);
        Assert.Contains("## Missing", error);
        Assert.Contains("Updated security.", result is null ? string.Empty : result);
        Assert.DoesNotContain("Updated security.", result);
    }

    [Fact]
    public void TryValidate_RejectsDuplicateTargetsAndInvalidOperations()
    {
        using var duplicate = JsonDocument.Parse("""
            [
              { "heading": "## Security", "op": "replace", "content": "A" },
              { "heading": "## Security", "op": "insert_after", "content": "B" }
            ]
            """);
        using var unsupported = JsonDocument.Parse("""
            [
              { "heading": "## Security", "op": "merge", "content": "A" }
            ]
            """);

        Assert.False(ProposalMarkdownEditApplier.TryValidate(duplicate.RootElement, out var duplicateError));
        Assert.Contains("more than once", duplicateError);
        Assert.False(ProposalMarkdownEditApplier.TryValidate(unsupported.RootElement, out var unsupportedError));
        Assert.Contains("replace, insert_after or delete", unsupportedError);
    }
}
