namespace Skypod.Core;

public sealed record BuildProgress(int Completed, int Total, string Message)
{
    public double Percentage => Total <= 0 ? 0 : (double)Completed / Total * 100;
}
