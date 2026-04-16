namespace Meridian.Mapping.Configuration;

internal sealed class SourceMemberConfigurationExpression : ISourceMemberConfigurationExpression
{
    internal bool ShouldValidate { get; private set; } = true;

    public void DoNotValidate()
    {
        ShouldValidate = false;
    }
}
