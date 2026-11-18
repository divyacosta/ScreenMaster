namespace ScreenMaster.UI
{
    /// <summary>ComboBox item: a stored key and the label shown.</summary>
    public sealed record Choice(string Key, string Label)
    {
        public override string ToString() => Label;
    }
}
