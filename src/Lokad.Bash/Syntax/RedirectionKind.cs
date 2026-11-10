namespace Lokad.Bash.Syntax;

/// <summary>
/// Identifies the redirection operator used in a redirection node.
/// </summary>
/// <remarks>
/// The full set of redirections is described in ; this initial
/// set will be expanded as support grows.
/// </remarks>
public enum RedirectionKind
{
    /// <summary>
    /// Input redirection: &lt;.
    /// </summary>
    Input,

    /// <summary>
    /// Output redirection: &gt;.
    /// </summary>
    Output,

    /// <summary>
    /// Output append redirection: &gt;&gt;.
    /// </summary>
    OutputAppend,

    /// <summary>
    /// Read/write redirection: &lt;&gt;.
    /// </summary>
    InputOutput,

    /// <summary>
    /// Here-document redirection: &lt;&lt;.
    /// </summary>
    HereDocument,

    /// <summary>
    /// Here-document with tab-stripping: &lt;&lt;-.
    /// </summary>
    HereDocumentStripTabs,

    /// <summary>
    /// Here-string redirection: &lt;&lt;&lt;.
    /// </summary>
    HereString,

    /// <summary>
    /// Force overwrite output: &gt;|.
    /// </summary>
    ForceOutput,

    /// <summary>
    /// Duplicate output descriptor: &gt;&amp;.
    /// </summary>
    DuplicateOutput,

    /// <summary>
    /// Duplicate input descriptor: &lt;&amp;.
    /// </summary>
    DuplicateInput,

    /// <summary>
    /// Close output descriptor: &gt;&amp;-.
    /// </summary>
    CloseOutput,

    /// <summary>
    /// Close input descriptor: &lt;&amp;-.
    /// </summary>
    CloseInput,

    /// <summary>
    /// Redirect stdout and stderr: &amp;&gt;.
    /// </summary>
    OutputAndError,

    /// <summary>
    /// Append stdout and stderr: &amp;&gt;&gt;.
    /// </summary>
    OutputAndErrorAppend,

    /// <summary>
    /// Allocate output descriptor to a variable: {varname}&gt;.
    /// </summary>
    AllocateOutput,

    /// <summary>
    /// Allocate input descriptor to a variable: {varname}&lt;.
    /// </summary>
    AllocateInput
}
