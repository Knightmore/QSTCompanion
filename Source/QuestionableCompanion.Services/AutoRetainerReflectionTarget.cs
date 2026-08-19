using System;
using System.Runtime.Loader;

namespace QuestionableCompanion.Services;

internal sealed record AutoRetainerReflectionTarget(object Plugin, AssemblyLoadContext LoadContext, Func<object?> ReadEzConfig, Action Save);
