namespace QuestionableCompanion.Data.HuntLogs;

public sealed class HuntLog
{
	public HuntMark?[,] HuntMarks { get; } = new HuntMark[5, 40];
}
