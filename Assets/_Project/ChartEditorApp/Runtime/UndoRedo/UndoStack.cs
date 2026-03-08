// UndoStack.cs
// Command-pattern undo/redo stack for all chart editor data mutations.
//
// Spec §4.2: "Full undo/redo for all chart edits."
//
// Design:
//   – Every user action that mutates EditorProject.Data is represented as an IEditorCommand.
//   – Execute() applies the change and stores enough data to Undo() it.
//   – The UndoStack holds an undo list and a redo list.
//   – On Execute: clear redo list, push to undo list.
//   – On Undo: pop from undo, push to redo, call command.Undo().
//   – On Redo: pop from redo, push to undo, call command.Execute().
//
// IEditorCommand implementations must:
//   – Be data-only (no MonoBehaviour, no scene refs).
//   – Capture all state needed to undo (e.g., old value, new value).
//   – Call project.MarkDirty() in both Execute() and Undo().
//
// No UnityEditor APIs used (spec: ChartEditorApp must not use UnityEditor namespace).

using System;
using System.Collections.Generic;

namespace RhythmicFlow.ChartEditor
{
    // -----------------------------------------------------------------------
    // IEditorCommand
    // -----------------------------------------------------------------------

    /// <summary>
    /// Contract for all undoable editor operations.
    /// </summary>
    public interface IEditorCommand
    {
        /// <summary>Human-readable description for undo/redo UI labels.</summary>
        string Description { get; }

        /// <summary>Applies the change to the project. Called once when the action is performed,
        /// and again on Redo.</summary>
        void Execute(EditorProject project);

        /// <summary>Reverts the change. Called on Undo.</summary>
        void Undo(EditorProject project);
    }

    // -----------------------------------------------------------------------
    // UndoStack
    // -----------------------------------------------------------------------

    /// <summary>
    /// Manages undo and redo stacks for all chart editor data operations.
    /// </summary>
    public class UndoStack
    {
        private readonly int               _maxDepth;
        private readonly Stack<IEditorCommand> _undoStack;
        private readonly Stack<IEditorCommand> _redoStack;

        // -------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------

        /// <param name="maxDepth">Maximum undo history depth (oldest entries discarded).</param>
        public UndoStack(int maxDepth = 200)
        {
            _maxDepth  = maxDepth;
            _undoStack = new Stack<IEditorCommand>(_maxDepth);
            _redoStack = new Stack<IEditorCommand>(_maxDepth);
        }

        // -------------------------------------------------------------------
        // State queries
        // -------------------------------------------------------------------

        /// <summary>True when there is at least one action to undo.</summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>True when there is at least one action to redo.</summary>
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>Description of the next action to undo (or null).</summary>
        public string NextUndoDescription =>
            _undoStack.Count > 0 ? _undoStack.Peek().Description : null;

        /// <summary>Description of the next action to redo (or null).</summary>
        public string NextRedoDescription =>
            _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

        // -------------------------------------------------------------------
        // Execute a new command
        // -------------------------------------------------------------------

        /// <summary>
        /// Executes <paramref name="command"/> immediately, pushes it to the undo stack,
        /// and clears the redo stack (any redoable future is lost when a new action is taken).
        /// </summary>
        public void Execute(IEditorCommand command, EditorProject project)
        {
            if (command == null) { throw new ArgumentNullException(nameof(command)); }
            if (project  == null) { throw new ArgumentNullException(nameof(project)); }

            command.Execute(project);

            // Clear redo stack on new action.
            _redoStack.Clear();

            // Enforce max depth by trimming the oldest undo entry.
            if (_undoStack.Count >= _maxDepth)
            {
                // Stack doesn't allow arbitrary removal; rebuild without the bottom item.
                TrimUndoStack();
            }

            _undoStack.Push(command);
        }

        // -------------------------------------------------------------------
        // Undo
        // -------------------------------------------------------------------

        /// <summary>
        /// Undoes the most recent command and pushes it to the redo stack.
        /// No-op if the undo stack is empty.
        /// </summary>
        public void Undo(EditorProject project)
        {
            if (!CanUndo) { return; }

            IEditorCommand command = _undoStack.Pop();
            command.Undo(project);
            _redoStack.Push(command);
        }

        // -------------------------------------------------------------------
        // Redo
        // -------------------------------------------------------------------

        /// <summary>
        /// Re-executes the most recently undone command and pushes it back to the undo stack.
        /// No-op if the redo stack is empty.
        /// </summary>
        public void Redo(EditorProject project)
        {
            if (!CanRedo) { return; }

            IEditorCommand command = _redoStack.Pop();
            command.Execute(project);
            _undoStack.Push(command);
        }

        // -------------------------------------------------------------------
        // Clear
        // -------------------------------------------------------------------

        /// <summary>Clears both stacks (e.g., on new project or fresh file load).</summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        // -------------------------------------------------------------------
        // Internal: trim oldest undo entry
        // -------------------------------------------------------------------

        // C# Stack doesn't support removing the bottom item; we reconstruct.
        private void TrimUndoStack()
        {
            var temp  = new IEditorCommand[_undoStack.Count];
            _undoStack.CopyTo(temp, 0);   // temp[0] = top (newest), temp[last] = bottom (oldest)
            _undoStack.Clear();

            // Re-push all except the last (oldest).
            for (int i = temp.Length - 2; i >= 0; i--)
            {
                _undoStack.Push(temp[i]);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Built-in commands for common core operations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generic property-set command for any EditorProject.Data field mutation.
    /// Captures old and new values at construction time.
    /// </summary>
    public class SetPropertyCommand<T> : IEditorCommand
    {
        private readonly string  _description;
        private readonly T       _oldValue;
        private readonly T       _newValue;
        private readonly Action<EditorProject, T> _setter;

        public string Description => _description;

        public SetPropertyCommand(
            string description,
            T oldValue,
            T newValue,
            Action<EditorProject, T> setter)
        {
            _description = description;
            _oldValue    = oldValue;
            _newValue    = newValue;
            _setter      = setter;
        }

        public void Execute(EditorProject project)
        {
            _setter(project, _newValue);
            project.MarkDirty();
        }

        public void Undo(EditorProject project)
        {
            _setter(project, _oldValue);
            project.MarkDirty();
        }
    }

    /// <summary>
    /// Adds a DifficultyRecord to the project's difficulties list.
    /// </summary>
    public class AddDifficultyCommand : IEditorCommand
    {
        private readonly DifficultyRecord _record;

        public string Description => $"Add difficulty '{_record.difficultyId}'";

        public AddDifficultyCommand(DifficultyRecord record)
        {
            _record = record;
        }

        public void Execute(EditorProject project)
        {
            project.Data.difficulties.Add(_record);
            project.MarkDirty();
        }

        public void Undo(EditorProject project)
        {
            project.Data.difficulties.Remove(_record);
            project.MarkDirty();
        }
    }

    /// <summary>
    /// Removes a DifficultyRecord from the project's difficulties list.
    /// </summary>
    public class RemoveDifficultyCommand : IEditorCommand
    {
        private readonly DifficultyRecord _record;
        private          int              _removedIndex;

        public string Description => $"Remove difficulty '{_record.difficultyId}'";

        public RemoveDifficultyCommand(DifficultyRecord record)
        {
            _record = record;
        }

        public void Execute(EditorProject project)
        {
            _removedIndex = project.Data.difficulties.IndexOf(_record);

            if (_removedIndex >= 0)
            {
                project.Data.difficulties.RemoveAt(_removedIndex);
                project.MarkDirty();
            }
        }

        public void Undo(EditorProject project)
        {
            if (_removedIndex >= 0 && _removedIndex <= project.Data.difficulties.Count)
            {
                project.Data.difficulties.Insert(_removedIndex, _record);
                project.MarkDirty();
            }
        }
    }
}
