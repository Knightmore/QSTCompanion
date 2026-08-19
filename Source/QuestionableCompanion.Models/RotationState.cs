using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

public class RotationState
{
	private readonly object _lock = new object();

	private uint _currentStopQuestId;

	private List<string> _selectedCharacters = new List<string>();

	private string _currentCharacter = "";

	private string _nextCharacter = "";

	private List<string> _completedCharacters = new List<string>();

	private List<string> _remainingCharacters = new List<string>();

	private RotationPhase _phase;

	private DateTime _phaseStartTime = DateTime.Now;

	private string _errorMessage = "";

	private DateTime? _rotationStartTime;

	private bool _hasQuestBeenAccepted;

	private uint? _lastKnownQuestState;

	private bool _isSyncOnlyMode;

	private List<string> _skippedCharacters = new List<string>();

	public uint CurrentStopQuestId
	{
		get
		{
			lock (_lock)
			{
				return _currentStopQuestId;
			}
		}
		set
		{
			lock (_lock)
			{
				_currentStopQuestId = value;
			}
		}
	}

	public List<string> SelectedCharacters
	{
		get
		{
			lock (_lock)
			{
				return new List<string>(_selectedCharacters);
			}
		}
		set
		{
			lock (_lock)
			{
				_selectedCharacters = new List<string>(value);
			}
		}
	}

	public string CurrentCharacter
	{
		get
		{
			lock (_lock)
			{
				return _currentCharacter;
			}
		}
		set
		{
			lock (_lock)
			{
				_currentCharacter = value;
			}
		}
	}

	public string NextCharacter
	{
		get
		{
			lock (_lock)
			{
				return _nextCharacter;
			}
		}
		set
		{
			lock (_lock)
			{
				_nextCharacter = value;
			}
		}
	}

	public List<string> CompletedCharacters
	{
		get
		{
			lock (_lock)
			{
				return new List<string>(_completedCharacters);
			}
		}
		set
		{
			lock (_lock)
			{
				_completedCharacters = new List<string>(value);
			}
		}
	}

	public List<string> RemainingCharacters
	{
		get
		{
			lock (_lock)
			{
				return new List<string>(_remainingCharacters);
			}
		}
		set
		{
			lock (_lock)
			{
				_remainingCharacters = new List<string>(value);
			}
		}
	}

	public List<string> SkippedCharacters
	{
		get
		{
			lock (_lock)
			{
				return new List<string>(_skippedCharacters);
			}
		}
		set
		{
			lock (_lock)
			{
				_skippedCharacters = new List<string>(value);
			}
		}
	}

	public RotationPhase Phase
	{
		get
		{
			lock (_lock)
			{
				return _phase;
			}
		}
		set
		{
			lock (_lock)
			{
				_phase = value;
			}
		}
	}

	public DateTime PhaseStartTime
	{
		get
		{
			lock (_lock)
			{
				return _phaseStartTime;
			}
		}
		set
		{
			lock (_lock)
			{
				_phaseStartTime = value;
			}
		}
	}

	public string ErrorMessage
	{
		get
		{
			lock (_lock)
			{
				return _errorMessage;
			}
		}
		set
		{
			lock (_lock)
			{
				_errorMessage = value;
			}
		}
	}

	public DateTime? RotationStartTime
	{
		get
		{
			lock (_lock)
			{
				return _rotationStartTime;
			}
		}
		set
		{
			lock (_lock)
			{
				_rotationStartTime = value;
			}
		}
	}

	public bool HasQuestBeenAccepted
	{
		get
		{
			lock (_lock)
			{
				return _hasQuestBeenAccepted;
			}
		}
		set
		{
			lock (_lock)
			{
				_hasQuestBeenAccepted = value;
			}
		}
	}

	public uint? LastKnownQuestState
	{
		get
		{
			lock (_lock)
			{
				return _lastKnownQuestState;
			}
		}
		set
		{
			lock (_lock)
			{
				_lastKnownQuestState = value;
			}
		}
	}

	public bool IsSyncOnlyMode
	{
		get
		{
			lock (_lock)
			{
				return _isSyncOnlyMode;
			}
		}
		set
		{
			lock (_lock)
			{
				_isSyncOnlyMode = value;
			}
		}
	}
}
