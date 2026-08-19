using System;

namespace QuestionableCompanion.Services;

public class StuckDetectedEventArgs : EventArgs
{
	public bool Handled { get; set; }
}
