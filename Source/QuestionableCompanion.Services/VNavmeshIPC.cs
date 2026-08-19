using System;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace QuestionableCompanion.Services;

public class VNavmeshIPC : IDisposable
{
	private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloorSubscriber;

	private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPointSubscriber;

	private readonly ICallGateSubscriber<bool> isReadySubscriber;

	private readonly ICallGateSubscriber<bool> isPathfindingSubscriber;

	private readonly ICallGateSubscriber<bool> navPathfindingSubscriber;

	private readonly ICallGateSubscriber<bool> pathIsRunningSubscriber;

	private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveToSubscriber;

	private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseToSubscriber;

	private readonly ICallGateSubscriber<object> pathStopSubscriber;

	private readonly ICallGateSubscriber<object> pathfindCancelAllSubscriber;

	public VNavmeshIPC(IDalamudPluginInterface pluginInterface)
	{
		pointOnFloorSubscriber = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
		nearestPointSubscriber = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
		isReadySubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
		isPathfindingSubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
		navPathfindingSubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
		pathIsRunningSubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
		pathfindAndMoveToSubscriber = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
		pathfindAndMoveCloseToSubscriber = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
		pathStopSubscriber = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
		pathfindCancelAllSubscriber = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
	}

	public bool IsReady()
	{
		try
		{
			return isReadySubscriber.InvokeFunc();
		}
		catch
		{
			return false;
		}
	}

	public bool IsPathfinding()
	{
		try
		{
			return isPathfindingSubscriber.InvokeFunc() || navPathfindingSubscriber.InvokeFunc();
		}
		catch
		{
			try
			{
				return navPathfindingSubscriber.InvokeFunc();
			}
			catch
			{
				return false;
			}
		}
	}

	public bool IsPathRunning()
	{
		try
		{
			return pathIsRunningSubscriber.InvokeFunc();
		}
		catch
		{
			return false;
		}
	}

	public bool TryGetActivity(out bool ready, out bool busy)
	{
		ready = false;
		busy = false;
		try
		{
			ready = isReadySubscriber.InvokeFunc();
		}
		catch
		{
			return false;
		}
		int num = 0;
		try
		{
			busy |= isPathfindingSubscriber.InvokeFunc();
			num++;
		}
		catch
		{
		}
		try
		{
			busy |= navPathfindingSubscriber.InvokeFunc();
			num++;
		}
		catch
		{
		}
		try
		{
			busy |= pathIsRunningSubscriber.InvokeFunc();
			num++;
		}
		catch
		{
		}
		return num > 0;
	}

	public Vector3? FindPointOnFloor(Vector3 position, bool allowUnlandable = false, float searchRadius = 10f)
	{
		try
		{
			return pointOnFloorSubscriber.InvokeFunc(position, allowUnlandable, searchRadius);
		}
		catch (Exception)
		{
			return null;
		}
	}

	public Vector3? FindNearestPoint(Vector3 position, float horizontalRadius = 10f, float verticalRadius = 5f)
	{
		try
		{
			return nearestPointSubscriber.InvokeFunc(position, horizontalRadius, verticalRadius);
		}
		catch (Exception)
		{
			return null;
		}
	}

	public bool PathfindAndMoveTo(Vector3 target, bool fly = true)
	{
		try
		{
			return pathfindAndMoveToSubscriber.InvokeFunc(target, fly);
		}
		catch (Exception)
		{
			return false;
		}
	}

	public bool PathfindAndMoveCloseTo(Vector3 target, bool fly = true, float range = 1f)
	{
		try
		{
			return pathfindAndMoveCloseToSubscriber.InvokeFunc(target, fly, range);
		}
		catch (Exception)
		{
			return PathfindAndMoveTo(target, fly);
		}
	}

	public void StopPathfinding()
	{
		try
		{
			pathStopSubscriber.InvokeAction();
		}
		catch
		{
		}
	}

	public void CancelAllPathfinding()
	{
		try
		{
			pathfindCancelAllSubscriber.InvokeAction();
		}
		catch
		{
		}
	}

	public void StopCompletely()
	{
		StopPathfinding();
		CancelAllPathfinding();
	}

	public void Dispose()
	{
	}
}
