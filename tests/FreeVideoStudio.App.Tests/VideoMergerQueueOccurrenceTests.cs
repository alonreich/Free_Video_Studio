// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// QUEUEOCCURRENCE_01 / CLIPACTIONS_01 — queue operations target the EXACT selected occurrence:
/// moving, removing, key-deleting, or dragging duplicate file paths (e.g. [A, B, A]) operates on the
/// targeted copy, preserving duplicate instances, identities, multiselection relative order, and effects.
/// </summary>
public sealed class VideoMergerQueueOccurrenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsMergerQueueTests_" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _programData;

    public VideoMergerQueueOccurrenceTests()
    {
        _programData = LiveCaptureDriver.IsolateProgramData(_root);
    }

    public void Dispose()
    {
        _programData.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private VideoMergerWindow CreateWindow()
    {
        return new VideoMergerWindow();
    }

    [AvaloniaFact]
    public void ToolbarMoveUpAndDown_MovesExactDuplicateOccurrence_AndRestoresSelection()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(a);

            var ids = window.CurrentQueueClipIds;
            Assert.Equal(3, ids.Count);
            var (idA1, idB, idA2) = (ids[0], ids[1], ids[2]);
            Assert.NotEqual(idA1, idA2);

            // Select second occurrence of A (index 2)
            window.SelectQueueRowForTesting(2);
            Assert.Equal(new[] { 2 }, window.SelectedQueueIndicesForTesting());

            // Move up: queue becomes [A, A, B]
            window.MoveVideoForTesting(-1);

            Assert.Equal(new[] { a, a, b }, window.VideoQueue);
            Assert.Equal(new[] { idA1, idA2, idB }, window.CurrentQueueClipIds);
            // Selection restored to moved occurrence at index 1
            Assert.Equal(new[] { 1 }, window.SelectedQueueIndicesForTesting());

            // Move down: queue becomes [A, B, A]
            window.MoveVideoForTesting(1);

            Assert.Equal(new[] { a, b, a }, window.VideoQueue);
            Assert.Equal(new[] { idA1, idB, idA2 }, window.CurrentQueueClipIds);
            // Selection restored to index 2
            Assert.Equal(new[] { 2 }, window.SelectedQueueIndicesForTesting());
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void BoundaryMoves_DisableButtons_AndNoOp()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(a);

            var initialIds = window.CurrentQueueClipIds.ToList();

            // Select top row (index 0)
            window.SelectQueueRowForTesting(0);
            var upBtn = window.FindControl<Button>("MoveUpButton");
            var downBtn = window.FindControl<Button>("MoveDownButton");
            Assert.NotNull(upBtn);
            Assert.NotNull(downBtn);

            Assert.False(upBtn.IsEnabled);
            Assert.True(downBtn.IsEnabled);

            // Attempting to move up is a no-op
            window.MoveVideoForTesting(-1);
            Assert.Equal(new[] { a, b, a }, window.VideoQueue);
            Assert.Equal(initialIds, window.CurrentQueueClipIds);

            // Select bottom row (index 2)
            window.SelectQueueRowForTesting(2);
            Assert.True(upBtn.IsEnabled);
            Assert.False(downBtn.IsEnabled);

            // Attempting to move down is a no-op
            window.MoveVideoForTesting(1);
            Assert.Equal(new[] { a, b, a }, window.VideoQueue);
            Assert.Equal(initialIds, window.CurrentQueueClipIds);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Multiselection_PreservesRelativeOrder_AtBoundariesAndInterior()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            string c = @"C:\c\c.mp4";
            // Queue: [A, B, A, C]
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(c);

            var (idA1, idB, idA2, idC) = (
                window.CurrentQueueClipIds[0],
                window.CurrentQueueClipIds[1],
                window.CurrentQueueClipIds[2],
                window.CurrentQueueClipIds[3]
            );

            // Select non-adjacent [B (1), C (3)]
            window.SelectQueueRowsForTesting(new[] { 1, 3 });
            var upBtn = window.FindControl<Button>("MoveUpButton");
            var downBtn = window.FindControl<Button>("MoveDownButton");
            Assert.NotNull(upBtn);
            Assert.NotNull(downBtn);

            Assert.True(upBtn.IsEnabled);
            Assert.False(downBtn.IsEnabled); // index 3 touches bottom

            // Move up: group moves to [0, 2] -> queue: [B, A, C, A]
            window.MoveVideoForTesting(-1);

            Assert.Equal(new[] { b, a, c, a }, window.VideoQueue);
            Assert.Equal(new[] { idB, idA1, idC, idA2 }, window.CurrentQueueClipIds);
            Assert.Equal(new[] { 0, 2 }, window.SelectedQueueIndicesForTesting());

            // Now index 0 touches top boundary
            Assert.False(upBtn.IsEnabled);
            Assert.True(downBtn.IsEnabled);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ToolbarRemove_RemovesExactSelectedOccurrence_WithoutAffectingOtherCopy()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(a);

            var (idA1, idB, idA2) = (
                window.CurrentQueueClipIds[0],
                window.CurrentQueueClipIds[1],
                window.CurrentQueueClipIds[2]
            );

            // Select second A (index 2)
            window.SelectQueueRowForTesting(2);
            window.OnToolbarRemoveFlyoutOpened();

            // Execute remove
            window.ExecuteRemoveSelectedForTesting();

            Assert.Equal(2, window.VideoQueue.Count);
            Assert.Equal(new[] { a, b }, window.VideoQueue);
            Assert.Equal(new[] { idA1, idB }, window.CurrentQueueClipIds);
            Assert.DoesNotContain(idA2, window.CurrentQueueClipIds);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void KeyboardDelete_RemovesExactSelectedOccurrence()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(a);

            var (idA1, idB, idA2) = (
                window.CurrentQueueClipIds[0],
                window.CurrentQueueClipIds[1],
                window.CurrentQueueClipIds[2]
            );

            // Select first A (index 0)
            window.RemoveConfirmOverride = () => Task.FromResult(true);
            window.SelectQueueRowForTesting(0);

            var keyEvent = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Delete,
                KeyModifiers = KeyModifiers.None
            };
            window.TriggerClipActionsKeyDownForTesting(keyEvent);

            Assert.True(keyEvent.Handled);
            Assert.Equal(2, window.VideoQueue.Count);
            Assert.Equal(new[] { b, a }, window.VideoQueue);
            Assert.Equal(new[] { idB, idA2 }, window.CurrentQueueClipIds);
            Assert.DoesNotContain(idA1, window.CurrentQueueClipIds);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ContextMenu_RightClickUnselectedDuplicate_SelectsIt_AndExecutesAction()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(a);

            var (idA1, idB, idA2) = (
                window.CurrentQueueClipIds[0],
                window.CurrentQueueClipIds[1],
                window.CurrentQueueClipIds[2]
            );

            // Initially index 0 is selected
            window.SelectQueueRowForTesting(0);
            Assert.Equal(new[] { 0 }, window.SelectedQueueIndicesForTesting());

            // User right-clicks second A (index 2): selection updates to index 2 and context menu opens
            window.SelectQueueRowForTesting(2);
            window.OnQueueContextMenuOpened();
            Assert.Equal(new[] { 2 }, window.SelectedQueueIndicesForTesting());

            // Context menu Move Earlier (-1) is clicked
            window.MoveVideoForTesting(-1);

            Assert.Equal(new[] { a, a, b }, window.VideoQueue);
            Assert.Equal(new[] { idA1, idA2, idB }, window.CurrentQueueClipIds);
            Assert.Equal(new[] { 1 }, window.SelectedQueueIndicesForTesting());
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RowChangesWhileConfirmationIsOpen_SafelyAbandons_NeverDeletesOtherCopy()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(a);

            var (idA1, idB, idA2) = (
                window.CurrentQueueClipIds[0],
                window.CurrentQueueClipIds[1],
                window.CurrentQueueClipIds[2]
            );

            // Select second A (index 2)
            window.SelectQueueRowForTesting(2);
            var capturedIds = new List<Guid> { idA2 };

            // While dialog is pending, row 2 is removed externally (e.g. background/undo)
            window.VideoQueue.RemoveAt(2);

            // Dialog confirms remove on capturedIds
            bool removed = window.RemoveQueueClipsForTesting(capturedIds);

            // Safely abandoned
            Assert.False(removed);
            // Queue remains [A, B] and first A (idA1) was NOT removed in place of idA2
            Assert.Equal(new[] { a, b }, window.VideoQueue);
            Assert.Equal(new[] { idA1, idB }, window.CurrentQueueClipIds);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AdjacentDuplicates_SurviveIndependentRemovalAndReorder()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(a);

            var (id1, id2, id3) = (
                window.CurrentQueueClipIds[0],
                window.CurrentQueueClipIds[1],
                window.CurrentQueueClipIds[2]
            );

            // Select middle duplicate (index 1)
            window.SelectQueueRowForTesting(1);
            window.OnToolbarRemoveFlyoutOpened();
            window.ExecuteRemoveSelectedForTesting();

            Assert.Equal(2, window.VideoQueue.Count);
            Assert.Equal(new[] { id1, id3 }, window.CurrentQueueClipIds);

            // Select second remaining duplicate (index 1, id3) and move up
            window.SelectQueueRowForTesting(1);
            window.MoveVideoForTesting(-1);

            Assert.Equal(new[] { id3, id1 }, window.CurrentQueueClipIds);
            Assert.Equal(new[] { 0 }, window.SelectedQueueIndicesForTesting());
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void InternalDragDrop_VanishedClipId_SafelyAbandons()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);

            var initialIds = window.CurrentQueueClipIds.ToList();

            var dataTransfer = new Avalonia.Input.DataTransfer();
            // Payload with an ID not in the queue
            dataTransfer.Add(Avalonia.Input.DataTransferItem.Create(
                VideoMergerWindow.VideoItemFormatForTesting, Guid.NewGuid().ToString()));

            var dropEvent = new Avalonia.Input.DragEventArgs(
                Avalonia.Input.DragDrop.DropEvent,
                dataTransfer,
                window,
                new Avalonia.Point(10, 10),
                KeyModifiers.None
            );

            window.TriggerDropForTesting(dropEvent);

            // Nothing moved
            Assert.Equal(initialIds, window.CurrentQueueClipIds);
            Assert.Equal(new[] { a, b }, window.VideoQueue);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MultiSelect_MoveUpAndDown_PreservesRelativeOrder_AndUpdatesSelection()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            string c = @"C:\c\c.mp4";
            string d = @"C:\c\d.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(c);
            window.VideoQueue.Add(d);

            var ids = window.CurrentQueueClipIds;
            var (idA, idB, idC, idD) = (ids[0], ids[1], ids[2], ids[3]);

            // Multi-select B and C (indices 1 and 2)
            window.SelectQueueRowsForTesting(new[] { 1, 2 });
            Assert.Equal(new[] { 1, 2 }, window.SelectedQueueIndicesForTesting());

            // Move Up: [A, B, C, D] -> [B, C, A, D]
            window.MoveVideoForTesting(-1);

            Assert.Equal(new[] { b, c, a, d }, window.VideoQueue);
            Assert.Equal(new[] { idB, idC, idA, idD }, window.CurrentQueueClipIds);
            Assert.Equal(new[] { 0, 1 }, window.SelectedQueueIndicesForTesting());

            // At top boundary: Move Up should no-op
            window.MoveVideoForTesting(-1);
            Assert.Equal(new[] { b, c, a, d }, window.VideoQueue);
            Assert.Equal(new[] { 0, 1 }, window.SelectedQueueIndicesForTesting());

            // Move Down: [B, C, A, D] -> [A, B, C, D]
            window.MoveVideoForTesting(1);
            Assert.Equal(new[] { a, b, c, d }, window.VideoQueue);
            Assert.Equal(new[] { idA, idB, idC, idD }, window.CurrentQueueClipIds);
            Assert.Equal(new[] { 1, 2 }, window.SelectedQueueIndicesForTesting());
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MultiSelect_DeleteKey_RemovesAllSelectedClips()
    {
        var window = CreateWindow();
        try
        {
            string a = @"C:\c\a.mp4";
            string b = @"C:\c\b.mp4";
            string c = @"C:\c\c.mp4";
            string d = @"C:\c\d.mp4";
            window.VideoQueue.Add(a);
            window.VideoQueue.Add(b);
            window.VideoQueue.Add(c);
            window.VideoQueue.Add(d);

            var ids = window.CurrentQueueClipIds;
            var (idA, idD) = (ids[0], ids[3]);

            // Disable confirmation dialog for test
            window.RemoveConfirmOverride = () => Task.FromResult(true);

            // Multi-select B and C (indices 1 and 2)
            window.SelectQueueRowsForTesting(new[] { 1, 2 });
            Assert.Equal(new[] { 1, 2 }, window.SelectedQueueIndicesForTesting());

            // Trigger Delete Key
            var deleteKey = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Delete,
                KeyModifiers = KeyModifiers.None
            };
            window.TriggerClipActionsKeyDownForTesting(deleteKey);

            // Remaining queue should be [A, D]
            Assert.Equal(new[] { a, d }, window.VideoQueue);
            Assert.Equal(new[] { idA, idD }, window.CurrentQueueClipIds);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }
}
