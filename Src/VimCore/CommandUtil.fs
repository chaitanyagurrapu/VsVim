﻿#light

namespace Vim
open Vim.Modes
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Outlining
open Microsoft.VisualStudio.Text.Formatting
open System.Text
open RegexPatternUtil
open VimCoreExtensions
open ITextEditExtensions
open StringBuilderExtensions

[<RequireQualifiedAccess>]
[<NoComparison>]
[<StructuralEquality>]
type internal NumberValue =
    | Decimal of int
    | Octal of int
    | Hex of int
    | Alpha of char

    with

    member x.NumberFormat =
        match x with
        | Decimal _ -> NumberFormat.Decimal
        | Octal _ -> NumberFormat.Octal
        | Hex _ -> NumberFormat.Hex
        | Alpha _ -> NumberFormat.Alpha

/// There are some commands which if began in normal mode must end in normal
/// mode (undo and redo).  In general this is easy, don't switch modes.  But often
/// the code needs to call out to 3rd party code which can change the mode by
/// altering the selection.
///
/// This is a simple IDisposable type which will put us back into normal mode
/// if this happens.
type internal NormalModeSelectionGuard
    (
        _vimBufferData: IVimBufferData
    ) =

    let _beganInNormalMode = _vimBufferData.VimTextBuffer.ModeKind = ModeKind.Normal

    member x.Dispose() =
        let selection = _vimBufferData.TextView.Selection
        if _beganInNormalMode && not selection.IsEmpty then
            selection.Clear()
            _vimBufferData.VimTextBuffer.SwitchMode ModeKind.Normal ModeArgument.None |> ignore

    interface System.IDisposable with
        member x.Dispose() = x.Dispose()

/// This type houses the functionality behind a large set of the available
/// Vim commands.
///
/// This type could be further broken down into 2-3 types (one util to support
/// the commands for specific modes).  But there is a lot of benefit to keeping
/// them together as it reduces the overhead of sharing common infrastructure.
///
/// I've debated back and forth about separating them out.  Thus far though I've
/// decided to keep them all together because while there is a large set of
/// functionality here there is very little state.  So long as I can keep the
/// amount of stored state low here I believe it counters the size of the type
type internal CommandUtil
    (
        _vimBufferData: IVimBufferData,
        _motionUtil: IMotionUtil,
        _commonOperations: ICommonOperations,
        _foldManager: IFoldManager,
        _insertUtil: IInsertUtil,
        _bulkOperations: IBulkOperations,
        _mouseDevice: IMouseDevice,
        _lineChangeTracker: ILineChangeTracker
    ) =

    let _vimTextBuffer = _vimBufferData.VimTextBuffer
    let _wordNavigator = _vimTextBuffer.WordNavigator
    let _textView = _vimBufferData.TextView
    let _textBuffer = _textView.TextBuffer
    let _bufferGraph = _textView.BufferGraph
    let _statusUtil = _vimBufferData.StatusUtil
    let _undoRedoOperations = _vimBufferData.UndoRedoOperations
    let _localSettings = _vimBufferData.LocalSettings
    let _windowSettings = _vimBufferData.WindowSettings
    let _globalSettings = _localSettings.GlobalSettings
    let _vim = _vimBufferData.Vim
    let _vimData = _vim.VimData
    let _vimHost = _vim.VimHost
    let _markMap = _vim.MarkMap
    let _registerMap = _vim.RegisterMap
    let _searchService = _vim.SearchService
    let _macroRecorder = _vim.MacroRecorder
    let _jumpList = _vimBufferData.JumpList
    let _editorOperations = _commonOperations.EditorOperations
    let _options = _commonOperations.EditorOptions

    let mutable _inRepeatLastChange = false

    /// The column of the caret
    member x.CaretColumn = SnapshotPointUtil.GetColumn x.CaretPoint

    /// The SnapshotPoint for the caret
    member x.CaretPoint = TextViewUtil.GetCaretPoint _textView

    /// The VirtualSnapshotPoint for the caret
    member x.CaretVirtualPoint = TextViewUtil.GetCaretVirtualPoint _textView

    /// The ITextSnapshotLine for the caret
    member x.CaretLine = TextViewUtil.GetCaretLine _textView

    /// The line number for the caret
    member x.CaretLineNumber = x.CaretLine.LineNumber

    /// The SnapshotLineRange for the caret line
    member x.CaretLineRange = x.CaretLine |> SnapshotLineRangeUtil.CreateForLine

    /// The SnapshotPoint and ITextSnapshotLine for the caret
    member x.CaretPointAndLine = TextViewUtil.GetCaretPointAndLine _textView

    /// The current ITextSnapshot instance for the ITextBuffer
    member x.CurrentSnapshot = _textBuffer.CurrentSnapshot

    /// Add count values to the specific word
    member x.AddToWord count =

        let allowAlpha = _localSettings.IsNumberFormatSupported NumberFormat.Alpha
        match x.GetNumberValueAtCaret() with
        | None ->
            _commonOperations.Beep()
        | Some (numberValue, span) ->

            // Calculate te new value of the number
            let text =
                match numberValue with
                | NumberValue.Alpha c -> c |> CharUtil.AlphaAdd count |> StringUtil.OfChar
                | NumberValue.Decimal number -> sprintf "%d" (number + count)
                | NumberValue.Octal number -> sprintf "0%o" (number + count)
                | NumberValue.Hex number -> sprintf "0x%x" (number + count)

            // Need a transaction here in order to properly position the caret.  After the
            // add the caret needs to be positioned on the last character in the number
            x.EditWithUndoTransaction "Add" (fun () ->

                _textBuffer.Replace(span.Span, text) |> ignore

                let position = span.Start.Position + text.Length - 1
                TextViewUtil.MoveCaretToPosition _textView position)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Apply the ITextEdit and returned the mapped position value from the resulting
    /// ITextSnapshot into the current ITextSnapshot.
    ///
    /// A number of commands will edit the text and calculate the position of the caret
    /// based on the edits about to be made.  It's possible for other extensions to listen
    /// to the events fired by an edit and make fix up edits.  This code accounts for that
    /// and returns the position mapped into the current ITextSnapshot
    member x.ApplyEditAndMapPoint (textEdit: ITextEdit) position =
        let editSnapshot = textEdit.Apply()
        let editPoint = SnapshotPoint(editSnapshot, position)
        let currentSnapshot = x.CurrentSnapshot
        match TrackingPointUtil.GetPointInSnapshot editPoint PointTrackingMode.Negative currentSnapshot with
        | None -> SnapshotPoint(currentSnapshot, 0)
        | Some point -> point

    member x.ApplyEditAndMapPosition (textEdit: ITextEdit) position =
        let point = x.ApplyEditAndMapPoint textEdit position
        point.Position

    /// Calculate the new RegisterValue for the provided one for put with indent
    /// operations.
    member x.CalculateIdentStringData (registerValue: RegisterValue) =

        // Get the indent string to apply to the lines which are indented
        let indent =
            x.CaretLine
            |> SnapshotLineUtil.GetIndentText
            |> _commonOperations.NormalizeBlanks

        // Adjust the indentation on a given line of text to have the indentation
        // previously calculated
        let adjustTextLine (textLine: TextLine) =
            let oldIndent = textLine.Text |> Seq.takeWhile CharUtil.IsBlank |> StringUtil.OfCharSeq
            let text = indent + (textLine.Text.Substring(oldIndent.Length))
            { textLine with Text = text }

        // Really a put after with indent is just a normal put after of the adjusted
        // register value.  So adjust here and forward on the magic
        let stringData =
            let stringData = registerValue.StringData
            match stringData with
            | StringData.Block _ ->
                // Block values don't participate in indentation of this manner
                stringData
            | StringData.Simple text ->
                match registerValue.OperationKind with
                | OperationKind.CharacterWise ->

                    // We only change lines after the first.  So break it into separate lines
                    // fix their indent and then produce the new value.
                    let lines = TextLine.GetTextLines text
                    let head = lines.Head
                    let rest = lines.Rest |> Seq.map adjustTextLine
                    let text =
                        let all = Seq.append (Seq.singleton head) rest
                        TextLine.CreateString all
                    StringData.Simple text

                | OperationKind.LineWise ->

                    // Change every line for a line wise operation
                    text
                    |> TextLine.GetTextLines
                    |> Seq.map adjustTextLine
                    |> TextLine.CreateString
                    |> StringData.Simple

        x.CreateRegisterValue stringData registerValue.OperationKind

    /// Calculate the VisualSpan value for the associated ITextBuffer given the
    /// StoreVisualSpan value
    member x.CalculateVisualSpan stored =

        match stored with
        | StoredVisualSpan.Line (count = count) ->
            // Repeating a LineWise operation just creates a span with the same
            // number of lines as the original operation
            let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
            VisualSpan.Line range

        | StoredVisualSpan.Character (lineCount = lineCount; lastLineLength = lastLineLength) ->
            let characterSpan = CharacterSpan(x.CaretPoint, lineCount, lastLineLength)
            VisualSpan.Character characterSpan
        | StoredVisualSpan.Block (width = width; height = height) ->
            // Need to rehydrate spans of length 'length' on 'count' lines from the
            // current caret position
            let blockSpan = BlockSpan(x.CaretVirtualPoint, _localSettings.TabStop, width, height)
            VisualSpan.Block blockSpan

    member x.CalculateDeleteOperation (result: MotionResult) =
        if Util.IsFlagSet result.MotionResultFlags MotionResultFlags.BigDelete then RegisterOperation.BigDelete
        else RegisterOperation.Delete

    /// Change the characters in the given span via the specified change kind
    member x.ChangeCaseSpanCore kind (editSpan: EditSpan) =

        let func =
            match kind with
            | ChangeCharacterKind.Rot13 -> CharUtil.ChangeRot13
            | ChangeCharacterKind.ToLowerCase -> CharUtil.ToLower
            | ChangeCharacterKind.ToUpperCase -> CharUtil.ToUpper
            | ChangeCharacterKind.ToggleCase -> CharUtil.ChangeCase

        use edit = _textBuffer.CreateEdit()
        editSpan.Spans
        |> Seq.map (SnapshotSpanUtil.GetPoints SearchPath.Forward)
        |> Seq.concat
        |> Seq.filter (fun p -> CharUtil.IsLetter (p.GetChar()))
        |> Seq.iter (fun p ->
            let change = func (p.GetChar()) |> StringUtil.OfChar
            edit.Replace(p.Position, 1, change) |> ignore)
        edit.Apply() |> ignore

    /// Change the caret line via the specified ChangeCharacterKind.
    member x.ChangeCaseCaretLine kind =

        // The caret should be positioned on the first non-blank space in
        // the line.  If the line is completely blank the caret should
        // not be moved.  Caret should be in the same place for undo / redo
        // so move before and inside the transaction
        let position =
            x.CaretLine
            |> SnapshotLineUtil.GetPoints SearchPath.Forward
            |> Seq.skipWhile SnapshotPointUtil.IsWhiteSpace
            |> Seq.map SnapshotPointUtil.GetPosition
            |> SeqUtil.tryHeadOnly

        let maybeMoveCaret () =
            match position with
            | Some position -> TextViewUtil.MoveCaretToPosition _textView position
            | None -> ()

        maybeMoveCaret()
        x.EditWithUndoTransaction "Change" (fun () ->
            x.ChangeCaseSpanCore kind (EditSpan.Single x.CaretLine.Extent)
            maybeMoveCaret())

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Change the case of the specified motion
    member x.ChangeCaseMotion kind (result: MotionResult) =

        // The caret should be placed at the start of the motion for both
        // undo / redo so move before and inside the transaction
        TextViewUtil.MoveCaretToPoint _textView result.Span.Start
        x.EditWithUndoTransaction "Change" (fun () ->
            x.ChangeCaseSpanCore kind result.EditSpan
            TextViewUtil.MoveCaretToPosition _textView result.Span.Start.Position)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Change the case of the current caret point
    member x.ChangeCaseCaretPoint kind count =

        // The caret should be placed after the caret point but only
        // for redo.  Undo should move back to the current position so
        // don't move until inside the transaction
        x.EditWithUndoTransaction "Change" (fun () ->

            let span =
                let endPoint = SnapshotLineUtil.GetColumnOrEnd (x.CaretColumn + count) x.CaretLine
                SnapshotSpan(x.CaretPoint, endPoint)

            let editSpan = EditSpan.Single span
            x.ChangeCaseSpanCore kind editSpan

            // Move the caret but make sure to respect the 'virtualedit' option
            let point = SnapshotPoint(x.CurrentSnapshot, span.End.Position)
            _commonOperations.MoveCaretToPoint point ViewFlags.VirtualEdit)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Change the case of the selected text.
    member x.ChangeCaseVisual kind (visualSpan: VisualSpan) =

        // The caret should be positioned at the start of the VisualSpan for both
        // undo / redo so move it before and inside the transaction
        let point = visualSpan.Start
        let moveCaret () = TextViewUtil.MoveCaretToPosition _textView point.Position
        moveCaret()
        x.EditWithUndoTransaction "Change" (fun () ->
            x.ChangeCaseSpanCore kind visualSpan.EditSpan
            moveCaret())

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Delete the specified motion and enter insert mode
    member x.ChangeMotion registerName (result: MotionResult) =

        // This command has legacy / special case behavior for forward word motions.  It will
        // not delete any trailing whitespace in the span if the motion is created for a forward
        // word motion. This behavior is detailed in the :help WORD section of the gVim
        // documentation and is likely legacy behavior coming from the original vi
        // implementation.  A larger discussion thread is available here
        // http://groups.google.com/group/vim_use/browse_thread/thread/88b6499bbcb0878d/561dfe13d3f2ef63?lnk=gst&q=whitespace+cw#561dfe13d3f2ef63

        let span =
            if result.IsAnyWordMotion && result.IsForward then
                let point =
                    result.Span
                    |> SnapshotSpanUtil.GetPoints SearchPath.Backward
                    |> Seq.tryFind (fun x -> x.GetChar() |> CharUtil.IsWhiteSpace |> not)
                match point with
                | Some(p) ->
                    let endPoint =
                        p
                        |> SnapshotPointUtil.TryAddOne
                        |> OptionUtil.getOrDefault (SnapshotUtil.GetEndPoint (p.Snapshot))
                    SnapshotSpan(result.Span.Start, endPoint)
                | None -> result.Span
            elif result.OperationKind = OperationKind.LineWise then
                // If the change command ends inside a line break then the actual delete operation
                // is backed up so that it leaves a single blank line after the delete operation.  This
                // allows the insert to begin on a blank line
                match SnapshotSpanUtil.GetLastIncludedPoint result.Span with
                | Some point ->
                    if SnapshotPointUtil.IsInsideLineBreak point then
                        let line = SnapshotPointUtil.GetContainingLine point
                        SnapshotSpan(result.Span.Start, line.End)
                    else
                        result.Span
                | None -> result.Span
            else
                result.Span

        // Use an undo transaction to preserve the caret position.  Experiments show that the rules
        // for caret undo should be
        //  1. start of the change if motion is character wise
        //  2. start of second line if motion is line wise
        let point =
            match result.MotionKind with
            | MotionKind.CharacterWiseExclusive -> span.Start
            | MotionKind.CharacterWiseInclusive -> span.Start
            | MotionKind.LineWise ->
                let startLine = SnapshotSpanUtil.GetStartLine span
                let line =
                    SnapshotUtil.TryGetLine span.Snapshot (startLine.LineNumber + 1)
                    |> OptionUtil.getOrDefault startLine
                line.Start

        TextViewUtil.MoveCaretToPoint _textView point
        let commandResult =
            x.EditWithLinkedChange "Change" (fun () ->
                _textBuffer.Delete(span.Span) |> ignore
                TextViewUtil.MoveCaretToPosition _textView span.Start.Position)

        // Now that the delete is complete update the register
        let value = x.CreateRegisterValue (StringData.OfSpan span) result.OperationKind
        let operation = x.CalculateDeleteOperation result
        _commonOperations.SetRegisterValue registerName operation value

        commandResult

    /// Delete 'count' lines and begin insert mode.  The documentation of this command
    /// and behavior are a bit off.  It's documented like it behaves like 'dd + insert mode'
    /// but behaves more like ChangeTillEndOfLine but linewise and deletes the entire
    /// first line
    member x.ChangeLines count registerName =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        x.ChangeLinesCore range registerName

    /// Core routine for changing a set of lines in the ITextBuffer.  This is the backing function
    /// for changing lines in both normal and visual mode
    member x.ChangeLinesCore (range: SnapshotLineRange) registerName =

        // Caret position for the undo operation depends on the number of lines which are in
        // range being deleted.  If there is a single line then we position it before the first
        // non space / tab character in the first line.  If there is more than one line then we
        // position it at the equivalent location in the second line.
        //
        // There appears to be no logical reason for this behavior difference but it exists
        let point =
            let line =
                if range.Count = 1 then
                    range.StartLine
                else
                    SnapshotUtil.GetLine range.Snapshot (range.StartLineNumber + 1)
            line
            |> SnapshotLineUtil.GetPoints SearchPath.Forward
            |> Seq.skipWhile SnapshotPointUtil.IsBlank
            |> SeqUtil.tryHeadOnly
        match point with
        | None -> ()
        | Some point -> TextViewUtil.MoveCaretToPoint _textView point

        // Start an edit transaction to get the appropriate undo / redo behavior for the
        // caret movement after the edit.
        x.EditWithLinkedChange "ChangeLines" (fun () ->

            // Actually delete the text and position the caret
            _textBuffer.Delete(range.Extent.Span) |> ignore
            x.MoveCaretToDeletedLineStart range.StartLine

            // Update the register now that the operation is complete.  Register value is odd here
            // because we really didn't delete linewise but it's required to be a linewise
            // operation.
            let stringData = range.Extent.GetText() |> StringData.Simple
            let value = x.CreateRegisterValue stringData OperationKind.LineWise
            _commonOperations.SetRegisterValue registerName RegisterOperation.Delete value)

    /// Delete the selected lines and begin insert mode (implements the 'S', 'C' and 'R' visual
    /// mode commands.  This is very similar to DeleteLineSelection except that block deletion
    /// can be special cased depending on the command it's used in
    member x.ChangeLineSelection registerName (visualSpan: VisualSpan) specialCaseBlock =

        // The majority of cases simply delete a SnapshotLineRange directly.  Handle that here
        let deleteRange (range: SnapshotLineRange) =

            // In an undo the caret position has 2 cases.
            //  - Single line range: Start of the first line
            //  - Multiline range: Start of the second line.
            let point =
                if range.Count = 1 then
                    range.StartLine.Start
                else
                    let next = SnapshotUtil.GetLine range.Snapshot (range.StartLineNumber + 1)
                    next.Start
            TextViewUtil.MoveCaretToPoint _textView point

            let commandResult = x.EditWithLinkedChange "ChangeLines" (fun () ->
                _textBuffer.Delete(range.Extent.Span) |> ignore
                x.MoveCaretToDeletedLineStart range.StartLine)

            (EditSpan.Single range.Extent, commandResult)

        // The special casing of block deletion is handled here
        let deleteBlock (col: NonEmptyCollection<SnapshotOverlapSpan>) =

            // First step is to change the SnapshotSpan instances to extent from the start to the
            // end of the current line
            let col = col |> NonEmptyCollectionUtil.Map (fun span ->
                let line = SnapshotPointUtil.GetContainingLine span.Start.Point
                let span = SnapshotSpan(span.Start.Point, line.End)
                SnapshotOverlapSpan(span))

            // Caret should be positioned at the start of the span for undo
            TextViewUtil.MoveCaretToPoint _textView col.Head.Start.Point

            let commandResult = x.EditWithLinkedChange "ChangeLines" (fun () ->
                let edit = _textBuffer.CreateEdit()
                col |> Seq.iter (fun span -> edit.Delete(span) |> ignore)
                let position = x.ApplyEditAndMapPosition edit col.Head.Start.Point.Position
                TextViewUtil.MoveCaretToPosition _textView position)

            (EditSpan.Block col, commandResult)

        // Dispatch to the appropriate type of edit
        let editSpan, commandResult =
            match visualSpan with
            | VisualSpan.Character characterSpan ->
                characterSpan.Span |> SnapshotLineRangeUtil.CreateForSpan |> deleteRange
            | VisualSpan.Line range ->
                deleteRange range
            | VisualSpan.Block blockSpan ->
                if specialCaseBlock then deleteBlock blockSpan.BlockOverlapSpans
                else visualSpan.EditSpan.OverarchingSpan |> SnapshotLineRangeUtil.CreateForSpan |> deleteRange

        let value = x.CreateRegisterValue (StringData.OfEditSpan editSpan) OperationKind.LineWise
        _commonOperations.SetRegisterValue registerName RegisterOperation.Delete value

        commandResult

    /// Delete till the end of the line and start insert mode
    member x.ChangeTillEndOfLine count registerName =

        // The actual text edit portion of this operation is identical to the
        // DeleteTillEndOfLine operation.  There is a difference though in the
        // positioning of the caret.  DeleteTillEndOfLine needs to consider the virtual
        // space settings since it remains in normal mode but change does not due
        // to it switching to insert mode
        let caretPosition = x.CaretPoint.Position
        x.EditWithLinkedChange "ChangeTillEndOfLine" (fun () ->
            x.DeleteTillEndOfLineCore count registerName

            // Move the caret back to it's original position.  Don't consider virtual
            // space here since we're switching to insert mode
            let point = SnapshotPoint(x.CurrentSnapshot, caretPosition)
            _commonOperations.MoveCaretToPoint point ViewFlags.None)

    /// Delete the selected text in Visual Mode and begin insert mode with a linked
    /// transaction.
    member x.ChangeSelection registerName (visualSpan: VisualSpan) =

        match visualSpan with
        | VisualSpan.Character _ ->
            // For block and character modes the change selection command is simply a
            // delete of the span and move into insert mode.
            //
            // Caret needs to be positioned at the front of the span in undo so move it
            // before we create the transaction
            TextViewUtil.MoveCaretToPoint _textView visualSpan.Start
            x.EditWithLinkedChange "ChangeSelection" (fun() ->
                x.DeleteSelection registerName visualSpan |> ignore)
        | VisualSpan.Block blockSpan ->
            // Change in block mode has behavior very similar to Shift + Insert.  It needs
            // to be a change followed by a transition into insert where the insert actions
            // are repeated across the block span

            // Caret needs to be positioned at the front of the span in undo so move it
            // before we create the transaction
            TextViewUtil.MoveCaretToPoint _textView visualSpan.Start
            x.EditBlockWithLinkedChange "Change Block" blockSpan false (fun () ->
                x.DeleteSelection registerName visualSpan |> ignore)

        | VisualSpan.Line range -> x.ChangeLinesCore range registerName

    /// Close a single fold under the caret
    member x.CloseFoldInSelection (visualSpan: VisualSpan) =
        let range = visualSpan.LineRange
        let offset = range.StartLineNumber
        for i = 0 to range.Count - 1 do
            let line = SnapshotUtil.GetLine x.CurrentSnapshot (offset + i)
            _foldManager.CloseFold line.Start 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Close 'count' folds under the caret
    member x.CloseFoldUnderCaret count =
        _foldManager.CloseFold x.CaretPoint count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Close all of the folds in the buffer
    member x.CloseAllFolds() =
        let span = SnapshotUtil.GetExtent x.CurrentSnapshot
        _foldManager.CloseAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Close all folds under the caret
    member x.CloseAllFoldsUnderCaret () =
        let span = SnapshotSpan(x.CaretPoint, 0)
        _foldManager.CloseAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Close all folds in the selection
    member x.CloseAllFoldsInSelection (visualSpan: VisualSpan) =
        let span = visualSpan.LineRange.Extent
        _foldManager.CloseAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Close the IVimBuffer.  This is an implementation of the ZZ command and won't check
    /// for a dirty buffer
    member x.CloseBuffer() =
        _vimHost.Close _textView
        CommandResult.Completed ModeSwitch.NoSwitch

    member x.CloseWindow() =
        _commonOperations.CloseWindowUnlessDirty()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Create a possibly LineWise register value with the specified string value at the given
    /// point.  This is factored out here because a LineWise value in vim should always
    /// end with a new line but we can't always guarantee the text we are working with
    /// contains a new line.  This normalizes out the process needed to make this correct
    /// while respecting the settings of the ITextBuffer
    member x.CreateRegisterValue stringData operationKind =
        _commonOperations.CreateRegisterValue x.CaretPoint stringData operationKind

    /// Delete 'count' characters after the cursor on the current line.  Caret should
    /// remain at it's original position
    member x.DeleteCharacterAtCaret count registerName =

        // Check for the case where the caret is past the end of the line.  Can happen
        // when 've=onemore'
        if x.CaretPoint.Position < x.CaretLine.End.Position then
            let endPoint = SnapshotLineUtil.GetColumnOrEnd (x.CaretColumn + count) x.CaretLine
            let span = SnapshotSpan(x.CaretPoint, endPoint)

            // Use a transaction so we can guarantee the caret is in the correct
            // position on undo / redo
            x.EditWithUndoTransaction "DeleteChar" (fun () ->
                let position = x.CaretPoint.Position
                let snapshot = TextBufferUtil.DeleteAndGetLatest _textBuffer span.Span

                // Need to respect the virtual edit setting here as we could have
                // deleted the last character on the line
                let point = SnapshotPoint(snapshot, position)
                _commonOperations.MoveCaretToPoint point ViewFlags.VirtualEdit)

            // Put the deleted text into the specified register
            let value = RegisterValue(StringData.OfSpan span, OperationKind.CharacterWise)
            _commonOperations.SetRegisterValue registerName RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete 'count' characters before the cursor on the current line.  Caret should be
    /// positioned at the begining of the span for undo / redo
    member x.DeleteCharacterBeforeCaret count registerName =

        let startPoint =
            let position = x.CaretPoint.Position - count
            if position < x.CaretLine.Start.Position then x.CaretLine.Start else SnapshotPoint(x.CurrentSnapshot, position)
        let span = SnapshotSpan(startPoint, x.CaretPoint)

        // Use a transaction so we can guarantee the caret is in the correct position.  We
        // need to position the caret to the start of the span before the transaction to
        // ensure it appears there during an undo
        TextViewUtil.MoveCaretToPoint _textView startPoint
        x.EditWithUndoTransaction "DeleteChar" (fun () ->
            let snapshot = TextBufferUtil.DeleteAndGetLatest _textBuffer span.Span
            TextViewUtil.MoveCaretToPosition _textView startPoint.Position)

        // Put the deleted text into the specified register once the delete completes
        let value = RegisterValue(StringData.OfSpan span, OperationKind.CharacterWise)
        _commonOperations.SetRegisterValue registerName RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete a fold under the caret
    member x.DeleteFoldUnderCaret () =
        _foldManager.DeleteFold x.CaretPoint
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete a fold from the selection
    member x.DeleteAllFoldInSelection (visualSpan: VisualSpan) =
        let span = visualSpan.LineRange.Extent
        _foldManager.DeleteAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete all folds under the caret
    member x.DeleteAllFoldsUnderCaret () =
        let span = SnapshotSpan(x.CaretPoint, 0)
        _foldManager.DeleteAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete all of the folds in the ITextBuffer
    member x.DeleteAllFoldsInBuffer () =
        let extent = SnapshotUtil.GetExtent x.CurrentSnapshot
        _foldManager.DeleteAllFolds extent
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete the selected text from the buffer and put it into the specified
    /// register.
    member x.DeleteLineSelection registerName (visualSpan: VisualSpan) =

        // For each of the 3 cases the caret should begin at the start of the
        // VisualSpan during undo so move the caret now.
        TextViewUtil.MoveCaretToPoint _textView visualSpan.Start

        // Start a transaction so we can manipulate the caret position during
        // an undo / redo
        let editSpan =
            x.EditWithUndoTransaction "Delete" (fun () ->

                use edit = _textBuffer.CreateEdit()
                let editSpan =
                    match visualSpan with
                    | VisualSpan.Character characterSpan ->
                        // Just extend the SnapshotSpan to the encompassing SnapshotLineRange
                        let range = SnapshotLineRangeUtil.CreateForSpan characterSpan.Span
                        let span = range.ExtentIncludingLineBreak
                        edit.Delete(span.Span) |> ignore
                        EditSpan.Single span
                    | VisualSpan.Line range ->
                        // Easiest case.  It's just the range
                        edit.Delete(range.ExtentIncludingLineBreak.Span) |> ignore
                        EditSpan.Single range.ExtentIncludingLineBreak
                    | VisualSpan.Block blockSpan ->

                        // The delete is from the start of the block selection util the end of
                        // te containing line
                        let collection =
                            blockSpan.BlockOverlapSpans
                            |> NonEmptyCollectionUtil.Map (fun span ->
                                let line = SnapshotPointUtil.GetContainingLine span.Start.Point
                                let endPoint = SnapshotOverlapPoint(line.End)
                                SnapshotOverlapSpan(span.Start, endPoint))

                        // Actually perform the deletion
                        collection |> Seq.iter (fun span -> edit.Delete(span) |> ignore)

                        EditSpan.Block collection

                let point = x.ApplyEditAndMapPoint edit visualSpan.Start.Position
                _commonOperations.MoveCaretToPoint point ViewFlags.VirtualEdit

                editSpan)

        let value = x.CreateRegisterValue (StringData.OfEditSpan editSpan) OperationKind.LineWise
        _commonOperations.SetRegisterValue registerName RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Delete the highlighted text from the buffer and put it into the specified
    /// register.  The caret should be positioned at the beginning of the text for
    /// undo / redo
    member x.DeleteSelection registerName (visualSpan: VisualSpan) =
        let startPoint = visualSpan.Start

        // Use a transaction to guarantee caret position.  Caret should be at the start
        // during undo and redo so move it before the edit
        TextViewUtil.MoveCaretToPoint _textView startPoint
        x.EditWithUndoTransaction "DeleteSelection" (fun () ->
            use edit = _textBuffer.CreateEdit()
            visualSpan.OverlapSpans |> Seq.iter (fun overlapSpan ->
                let span = overlapSpan.OverarchingSpan

                // If the last included point in the SnapshotSpan is inside the line break
                // portion of a line then extend the SnapshotSpan to encompass the full
                // line break
                let span =
                    match SnapshotSpanUtil.GetLastIncludedPoint span with
                    | None ->
                        // Don't need to special case a 0 length span as it won't actually
                        // cause any change in the ITextBuffer
                        span
                    | Some last ->
                        if SnapshotPointUtil.IsInsideLineBreak last then
                            let line = SnapshotPointUtil.GetContainingLine last
                            SnapshotSpan(span.Start, line.EndIncludingLineBreak)
                        else
                            span

                edit.Delete(overlapSpan) |> ignore)

            let position = x.ApplyEditAndMapPosition edit startPoint.Position
            TextViewUtil.MoveCaretToPosition _textView position)

        // BTODO: The wrong text is being put into the register here.  It should include the overlap data
        let operationKind = visualSpan.OperationKind
        let value = x.CreateRegisterValue (StringData.OfEditSpan visualSpan.EditSpan) operationKind
        _commonOperations.SetRegisterValue registerName RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Delete count lines from the cursor.  The caret should be positioned at the start
    /// of the first line for both undo / redo
    member x.DeleteLines count registerName =
        _commonOperations.DeleteLines x.CaretLine count registerName
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete the specified motion of text
    member x.DeleteMotion registerName (result: MotionResult) =

        // The d{motion} command has an exception listed which is visible by typing ':help d' in
        // gVim.  In summary, if the motion is characterwise, begins and ends on different
        // lines and the start is preceeding by only whitespace and the end is followed
        // only by whitespace then it becomes a linewise motion for those lines.  This can be
        // demonstrated with the following example.  Caret is on the 'c', there is one space before
        // every word and 4 spaces after dog
        //
        //  cat
        //  dog
        //  fish
        //
        // Now execute 'd/  ' (2 spaces after the /).  This will delete the entire cat and dog
        // line
        let span, operationKind =
            let span = result.Span
            if result.LineRange.Count > 1 && result.OperationKind = OperationKind.CharacterWise then
                let startLine, lastLine = SnapshotSpanUtil.GetStartAndLastLine span
                let lastPoint =
                    if result.IsExclusive then result.End
                    else result.LastOrStart
                let endsInWhiteSpace =
                    lastPoint
                    |> SnapshotPointUtil.GetPoints SearchPath.Forward
                    |> Seq.takeWhile (fun point -> point.Position < lastLine.End.Position)
                    |> Seq.forall SnapshotPointUtil.IsWhiteSpace

                let inIndent =
                    let indentPoint = SnapshotLineUtil.GetIndentPoint startLine
                    span.Start.Position <= indentPoint.Position

                if endsInWhiteSpace && inIndent then
                    SnapshotSpanUtil.ExtendToFullLineIncludingLineBreak span, OperationKind.LineWise
                else
                    span, result.OperationKind
            else
                span, result.OperationKind

        // Caret should be placed at the start of the motion for both undo / redo so place it
        // before starting the transaction
        TextViewUtil.MoveCaretToPoint _textView span.Start
        x.EditWithUndoTransaction "Delete" (fun () ->
            _textBuffer.Delete(span.Span) |> ignore

            // Get the point on the current ITextSnapshot
            let point = SnapshotPoint(x.CurrentSnapshot, span.Start.Position)
            _commonOperations.MoveCaretToPoint point ViewFlags.VirtualEdit)

        // Update the register with the result so long as something was actually deleted
        // from the buffer
        if not span.IsEmpty then
            let value = x.CreateRegisterValue (StringData.OfSpan span) operationKind
            let operation = x.CalculateDeleteOperation result
            _commonOperations.SetRegisterValue registerName operation value

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete from the cursor to the end of the line and then 'count - 1' more lines into
    /// the buffer.  This is the implementation of the 'D' command
    member x.DeleteTillEndOfLine count registerName =

        let caretPosition = x.CaretPoint.Position

        // The caret is already at the start of the Span and it needs to be after the
        // delete so wrap it in an undo transaction
        x.EditWithUndoTransaction "Delete" (fun () ->
            x.DeleteTillEndOfLineCore count registerName

            // Move the caret back to the original position in the ITextBuffer.
            let point = SnapshotPoint(x.CurrentSnapshot, caretPosition)
            _commonOperations.MoveCaretToPoint point ViewFlags.VirtualEdit)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete from the caret to the end of the line and 'count - 1' more lines
    member x.DeleteTillEndOfLineCore count registerName =
        let span =
            if count = 1 then
                // Just deleting till the end of the caret line
                SnapshotSpan(x.CaretPoint, x.CaretLine.End)
            else
                // Grab a SnapshotLineRange for the 'count - 1' lines and combine in with
                // the caret start to get the span
                let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
                SnapshotSpan(x.CaretPoint, range.End)

        _textBuffer.Delete(span.Span) |> ignore

        // Delete is complete so update the register.  Strangely enough this is a character wise
        // operation even though it involves line deletion
        let value = RegisterValue(StringData.OfSpan span, OperationKind.CharacterWise)
        _commonOperations.SetRegisterValue registerName RegisterOperation.Delete value

    member x.DisplayCharacterCodePoint() =
        let point = SnapshotCodePoint(x.CaretPoint)
        if point.IsEndPoint then
            _commonOperations.Beep()
        elif point.IsInsideLineBreak then
            _statusUtil.OnStatus "NUL"
        else
            let text = point.GetText()
            let cp = point.CodePoint
            let str = sprintf "<%s> %d, %8x, %6o" text cp cp cp 
            _statusUtil.OnStatus str
        CommandResult.Completed ModeSwitch.NoSwitch

    member x.DisplayCharacterBytes() =
        let point = SnapshotCodePoint(x.CaretPoint)
        if point.IsEndPoint then
            _commonOperations.Beep()
        elif point.IsInsideLineBreak then
            _statusUtil.OnStatus "NUL"
        else
            let text = point.GetText()
            let bytes = Encoding.UTF8.GetBytes(text)
            let builder = StringBuilder()
            for i = 0 to bytes.Length - 1 do
                let str = sprintf "%02x" bytes.[i]
                if i > 0 then builder.AppendChar ' '
                builder.AppendString str
            _statusUtil.OnStatus (builder.ToString())
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Run the specified action with a wrapped undo transaction.  This is often necessary when
    /// an edit command manipulates the caret
    member x.EditWithUndoTransaction<'T> (name: string) (action: unit -> 'T): 'T =
        _undoRedoOperations.EditWithUndoTransaction name _textView action

    /// Used for the several commands which make an edit here and need the edit to be linked
    /// with the next insert mode change.
    member x.CreateTransactionForLinkedChange name action =
        let flags = LinkedUndoTransactionFlags.EndsWithInsert
        let transaction = _undoRedoOperations.CreateLinkedUndoTransactionWithFlags name flags

        try
            x.EditWithUndoTransaction name action
        with
            | _ ->
                // If the above throws we can't leave the transaction open else it will
                // break undo / redo in the ITextBuffer.  Close it here and
                // re-raise the exception
                transaction.Dispose()
                reraise()

        transaction

    /// Create an undo transaction, perform an action, and switch to normal insert mode
    member x.EditWithLinkedChange name action =
        let transaction = x.CreateTransactionForLinkedChange name action
        let arg = ModeArgument.InsertWithTransaction transaction
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, arg))

    /// Create an undo transaction, perform an action, and switch to insert mode with a count
    member x.EditCountWithLinkedChange name count action =
        let transaction = x.CreateTransactionForLinkedChange name action
        let arg = ModeArgument.InsertWithCountAndNewLine (count, transaction)
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, arg))

    /// Create an undo transaction, perform an action, and switch to block insert mode
    member x.EditBlockWithLinkedChange name blockSpan atEndOfLine action =
        let transaction = x.CreateTransactionForLinkedChange name action
        let arg = ModeArgument.InsertBlock (blockSpan, atEndOfLine, transaction)
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, arg))

    /// Used for commands which need to operate on the visual buffer and produce a SnapshotSpan
    /// to be mapped back to the text / edit buffer
    member x.EditWithVisualSnapshot action =
        let snapshotData = TextViewUtil.GetVisualSnapshotDataOrEdit _textView
        let span = action snapshotData
        BufferGraphUtil.MapSpanDownToSingle _bufferGraph span x.CurrentSnapshot

    /// Get a line range specifier
    member x.GetLineRangeSpecifier (lineRange: SnapshotLineRange) =
        let caretLine = TextViewUtil.GetCaretLine _textView
        let caretLineNumber = SnapshotLineUtil.GetLineNumber caretLine
        let extentLineRange = SnapshotLineRange.CreateForExtent x.CurrentSnapshot
        let lastLineNumber = extentLineRange.LastLineNumber

        let getLineSpecifier (line: int) =
            if line = 0 then
                "1"
            elif line = lastLineNumber then
                "$"
            elif line = caretLineNumber && not _localSettings.Number then
                "."
            else
                string(line + 1)

        if lineRange.StartLineNumber = 0 && lineRange.LastLineNumber = lastLineNumber then
            "%"
        elif lineRange.StartLineNumber = lineRange.LastLineNumber then
            getLineSpecifier lineRange.StartLineNumber
        else
            let startLineSpecifier = getLineSpecifier lineRange.StartLineNumber
            let lastLineSpecifier = getLineSpecifier lineRange.LastLineNumber
            startLineSpecifier + "," + lastLineSpecifier

    /// Build a partial filter command and switch to command mode
    member x.StartFilterCommand (specifier: string) =
        let partialCommand = specifier + "!"
        let modeArgument = ModeArgument.PartialCommand partialCommand
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Command, modeArgument))

    /// Filter the 'count' lines in the buffer
    member x.FilterLines count =
        let lineRange = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        let specifier = x.GetLineRangeSpecifier lineRange
        x.StartFilterCommand specifier

    /// Filter the lines in the Motion
    member x.FilterMotion (result: MotionResult) =
        let lineRange = result.LineRange
        let specifier = x.GetLineRangeSpecifier lineRange
        x.StartFilterCommand specifier

    /// Filter the selected lines
    member x.FilterLinesVisual (visualSpan: VisualSpan) =
        let lineRange = visualSpan.LineRange
        x.StartFilterCommand "'<,'>"

    /// Close a fold under the caret for 'count' lines
    member x.FoldLines count =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        _foldManager.CreateFold range
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Create a fold for the given MotionResult
    member x.FoldMotion (result: MotionResult) =
        _foldManager.CreateFold result.LineRange

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Fold the specified selection
    member x.FoldSelection (visualSpan: VisualSpan) =
        _foldManager.CreateFold visualSpan.LineRange

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Format the 'count' lines in the buffer
    member x.FormatLines count =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        _commonOperations.FormatLines range
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Format the selected lines
    member x.FormatLinesVisual (visualSpan: VisualSpan) =

        // Use a transaction so the formats occur as a single operation
        x.EditWithUndoTransaction "Format" (fun () ->
            visualSpan.Spans
            |> Seq.map SnapshotLineRangeUtil.CreateForSpan
            |> Seq.iter _commonOperations.FormatLines)

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Format the lines in the Motion
    member x.FormatMotion (result: MotionResult) =
        _commonOperations.FormatLines result.LineRange
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Get the appropriate register for the CommandData
    member x.GetRegister name = _commonOperations.GetRegister name

    /// Get the number value at the caret.  This is used for the CTRL-A and CTRL-X
    /// command so it will look forward on the current line for the first word
    ///
    /// TODO: Need to integrate the parsing functions here with that of the tokenizer
    /// which also parses out the same set of numbers
    member x.GetNumberValueAtCaret(): (NumberValue * SnapshotSpan) option=

        // Calculate the forward span of the line
        let span =
            let startPoint =
                SnapshotPointUtil.OrderAscending x.CaretPoint x.CaretLine.End
                |> fst
            SnapshotSpan(startPoint, x.CaretLine.End)

        // Get the number match out of the line with the given regex
        let getNumber numberValue numberPattern parseFunc =

            // Need to calculate the index on which to start looking for the number.  Move
            // past blanks as they don't factor in here
            let index =
                span
                |> SnapshotSpanUtil.GetPoints SearchPath.Forward
                |> Seq.skipWhile (fun point ->
                    if _localSettings.IsNumberFormatSupported(NumberFormat.Alpha) then
                        SnapshotPointUtil.IsBlank point
                    else
                        point |> SnapshotPointUtil.GetChar |> CharUtil.IsDigit |> not)
                |> SeqUtil.headOrDefault span.End
                |> SnapshotPointUtil.GetColumn

            let text = SnapshotLineUtil.GetText x.CaretLine
            System.Text.RegularExpressions.Regex.Matches(text, numberPattern)
            |> Seq.cast<System.Text.RegularExpressions.Match>
            |> Seq.tryFind (fun m -> index >= m.Index && index < (m.Index + m.Length))
            |> Option.map (fun m ->
                let span = SnapshotSpan(x.CurrentSnapshot, x.CaretLine.Start.Position + m.Index, m.Length)
                let succeeded, number = parseFunc m.Value
                if succeeded then
                    Some (numberValue number, span)
                else
                    None)
            |> OptionUtil.collapse

        // Get the point for a decimal number
        let getDecimal () =
            getNumber NumberValue.Decimal "(-?)\d+" (fun text -> System.Int32.TryParse(text))

        // Get the point for a hex number
        let getHex () =
            getNumber NumberValue.Hex "(-?)0x[a-f0-9]+" (fun text ->
                let isNegative, text =
                    if text.[0] = '-' then
                        true, text.Substring(3)
                    else
                        false, text.Substring(2)
                let succeeded, number = System.Int32.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.CurrentCulture)
                let number =
                    if succeeded && isNegative then
                        -number
                    else
                        number
                succeeded, number)

        let getOctal () =
            //getNumber = "0(0*)\d+"
            None

        let number =
            [
                (getHex, NumberFormat.Hex)
                (getOctal, NumberFormat.Octal)
                (getDecimal, NumberFormat.Decimal)
            ]
            |> Seq.map (fun (func, numberFormat) ->
                match _localSettings.IsNumberFormatSupported numberFormat, func() with
                | false, _ -> None
                | true, None -> None
                | true, Some tuple -> Some tuple)
            |> SeqUtil.filterToSome
            |> SeqUtil.tryHeadOnly

        match number with
        | Some _ ->
            number
        | None ->
            if _localSettings.IsNumberFormatSupported NumberFormat.Alpha then
                // Now check for alpha by going forward to the first alpha character
                span
                |> SnapshotSpanUtil.GetPoints SearchPath.Forward
                |> Seq.skipWhile (fun point -> point |> SnapshotPointUtil.GetChar |> CharUtil.IsAlpha |> not)
                |> Seq.map (fun point ->
                    let c = point.GetChar()
                    NumberValue.Alpha c, SnapshotSpan(point, 1))
                |> SeqUtil.tryHeadOnly
            else
                None

    /// Go to the definition of the word under the caret
    member x.GoToDefinition () =
        match _commonOperations.GoToDefinition() with
        | Result.Succeeded -> ()
        | Result.Failed(msg) -> _statusUtil.OnError msg

        CommandResult.Completed ModeSwitch.NoSwitch

    /// GoTo the file name under the cursor and possibly use a new window
    member x.GoToFileUnderCaret useNewWindow =
        if useNewWindow then _commonOperations.GoToFileInNewWindow()
        else _commonOperations.GoToFile()

        CommandResult.Completed ModeSwitch.NoSwitch

    /// GoTo the file name under the cursor using a new window (tab)
    member x.GoToFileInSelectionInNewWindow (visualSpan: VisualSpan) =
        let goToFile name =
            _commonOperations.GoToFileInNewWindow name
            CommandResult.Completed (ModeSwitch.SwitchMode ModeKind.Normal)
        match visualSpan with
        | VisualSpan.Character span -> span.Span.GetText() |> goToFile
        | VisualSpan.Line span -> span.GetText() |> goToFile
        | VisualSpan.Block _ -> CommandResult.Completed ModeSwitch.NoSwitch

    /// GoTo the file name under the cursor in the same window
    member x.GoToFileInSelection (visualSpan: VisualSpan) =
        let goToFile name =
            _commonOperations.GoToFile name
            CommandResult.Completed (ModeSwitch.SwitchMode ModeKind.Normal)
        match visualSpan with
        | VisualSpan.Character span -> span.Span.GetText() |> goToFile
        | VisualSpan.Line span -> span.GetText() |> goToFile
        | VisualSpan.Block _ -> CommandResult.Completed ModeSwitch.NoSwitch

    /// Go to the global declaration of the word under the caret
    member x.GoToGlobalDeclaration () =
        _commonOperations.GoToGlobalDeclaration()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Go to the local declaration of the word under the caret
    member x.GoToLocalDeclaration () =
        _commonOperations.GoToLocalDeclaration()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Go to the next tab in the specified direction
    member x.GoToNextTab path countOption =
        match path with
        | SearchPath.Forward ->
            match countOption with
            | Some count -> _commonOperations.GoToTab count
            | None -> _commonOperations.GoToNextTab path 1
        | SearchPath.Backward ->
            let count = countOption |> OptionUtil.getOrDefault 1
            _commonOperations.GoToNextTab SearchPath.Backward count

        CommandResult.Completed ModeSwitch.NoSwitch

    /// GoTo the ITextView in the specified direction
    member x.GoToWindow count direction =
        _vimHost.GoToWindow _textView count direction
        CommandResult.Completed ModeSwitch.NoSwitch

    /// GoTo the ITextView in the specified direction
    member x.GoToRecentView count =
        let vim = _vimBufferData.Vim
        match vim.TryGetRecentBuffer count with
        | None -> ()
        | Some vimBuffer ->
            let textView = vimBuffer.VimBufferData.TextView
            _vimHost.NavigateTo(textView.Caret.Position.VirtualBufferPosition) |> ignore
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Join 'count' lines in the buffer
    member x.JoinLines kind count =

        // An oddity of the join command is that the count 1 and 2 have the same effect.  Easiest
        // to treat both values as 2 since the math works out for all other values above 2
        let count = if count = 1 then 2 else count

        match SnapshotLineRangeUtil.CreateForLineAndCount x.CaretLine count with
        | None ->
            // If the count exceeds the length of the buffer then the operation should not
            // complete and a beep should be issued
            _commonOperations.Beep()
        | Some range ->
            // The caret should be positioned one after the second to last line in the
            // join.  It should have it's original position during an undo so don't
            // move the caret until we're inside the transaction
            x.EditWithUndoTransaction "Join" (fun () ->
                _commonOperations.Join range kind
                x.MoveCaretFollowingJoin range)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Join the selection of lines in the buffer
    member x.JoinSelection kind (visualSpan: VisualSpan) =
        let range = SnapshotLineRangeUtil.CreateForSpan visualSpan.EditSpan.OverarchingSpan

        // Extend the range to at least 2 lines if possible
        let range =
            if range.Count = 1 && range.LastLineNumber = SnapshotUtil.GetLastLineNumber range.Snapshot then
                // Can't extend
                range
            elif range.Count = 1 then
                // Extend it 1 line
                SnapshotLineRange(range.Snapshot, range.StartLineNumber, 2)
            else
                // Already at least 2 lines
                range

        if range.Count = 1 then
            // Can't join a single line
            _commonOperations.Beep()

            CommandResult.Completed ModeSwitch.NoSwitch
        else
            // The caret before the join should be positioned at the start of the VisualSpan
            TextViewUtil.MoveCaretToPoint _textView visualSpan.Start
            x.EditWithUndoTransaction "Join" (fun () ->
                _commonOperations.Join range kind
                x.MoveCaretFollowingJoin range)

            CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Invert the current selection
    member x.InvertSelection (visualSpan: VisualSpan) (streamSelectionSpan: VirtualSnapshotSpan) columnOnlyInBlock =

        // Do the selection change with the new values.  The only elements that must be correct
        // are the anchor point and caret position.  The selection tracker will be responsible
        // for properly setting to the character, line, block, etc ... once the command completes
        let changeSelection anchorPoint caretPoint =
            let extendIntoLineBreak = streamSelectionSpan.End.IsInVirtualSpace
            let visualSelection = VisualSelection.CreateForPoints visualSpan.VisualKind anchorPoint caretPoint _localSettings.TabStop
            let visualSelection = visualSelection.AdjustForExtendIntoLineBreak extendIntoLineBreak
            TextViewUtil.MoveCaretToPoint _textView caretPoint
            visualSelection.Select _textView
            _vimBufferData.VisualAnchorPoint <- Some (anchorPoint.Snapshot.CreateTrackingPoint(anchorPoint.Position, PointTrackingMode.Negative))

        match _vimBufferData.VisualAnchorPoint |> OptionUtil.map2 (TrackingPointUtil.GetPoint x.CurrentSnapshot) with
        | None -> ()
        | Some anchorPoint ->
            match visualSpan with
            | VisualSpan.Character characterSpan ->
                if characterSpan.Length > 1 then
                    if x.CaretPoint.Position > characterSpan.Start.Position then
                        changeSelection x.CaretPoint characterSpan.Start
                    else
                        let last =
                            match _globalSettings.SelectionKind with
                            | SelectionKind.Inclusive ->
                                Option.get characterSpan.Last
                            | SelectionKind.Exclusive ->
                                characterSpan.End
                        changeSelection characterSpan.Start last
            | VisualSpan.Line _ ->
                changeSelection x.CaretPoint anchorPoint
            | VisualSpan.Block blockSpan ->
                if columnOnlyInBlock then
                    // In this mode the caret simple jumps to the other end of the selection on the same
                    // line.  It doesn't switch caret + anchor, just the side the caret is on
                    let caretSpaces, anchorSpaces =
                        if (SnapshotPointUtil.GetColumn x.CaretPoint) >= (SnapshotPointUtil.GetColumn anchorPoint) then
                            blockSpan.ColumnSpaces, (blockSpan.Spaces + blockSpan.ColumnSpaces) - 1
                        else
                            (blockSpan.Spaces + blockSpan.ColumnSpaces) - 1, blockSpan.ColumnSpaces

                    let tabStop = _localSettings.TabStop
                    let newCaretPoint = SnapshotLineUtil.GetSpaceOrEnd x.CaretLine caretSpaces tabStop
                    let newAnchorPoint = SnapshotLineUtil.GetSpaceOrEnd (anchorPoint.GetContainingLine()) anchorSpaces tabStop
                    changeSelection newAnchorPoint newCaretPoint
                else
                    changeSelection x.CaretPoint anchorPoint

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Switch to insert mode after the caret
    member x.InsertAfterCaret count =
        match SnapshotPointUtil.TryGetNextCharacterSpanOnLine x.CaretPoint 1 with
        | Some nextPoint ->
            TextViewUtil.MoveCaretToPoint _textView nextPoint
        | None -> ()

        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count))

    /// Switch to Insert mode with the specified count
    member x.InsertBeforeCaret count =
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count))

    /// Switch to insert mode at the end of the line
    member x.InsertAtEndOfLine count =
        TextViewUtil.MoveCaretToPoint _textView x.CaretLine.End

        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count))

    /// Begin insert mode on the first non-blank character of the line.  Pass the count onto
    /// insert mode so it can duplicate the input
    member x.InsertAtFirstNonBlank count =
        let point =
            x.CaretLine
            |> SnapshotLineUtil.GetPoints SearchPath.Forward
            |> Seq.skipWhile SnapshotPointUtil.IsWhiteSpace
            |> SeqUtil.tryHeadOnly
            |> OptionUtil.getOrDefault x.CaretLine.End
        TextViewUtil.MoveCaretToPoint _textView point

        let switch = ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count)
        CommandResult.Completed switch

    /// Switch to insert mode at the start of the line
    member x.InsertAtStartOfLine count =
        TextViewUtil.MoveCaretToPoint _textView x.CaretLine.Start

        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count))

    /// Insert a line above the current caret line and begin insert mode at the start of that
    /// line
    member x.InsertLineAbove count =
        let savedCaretLine = x.CaretLine

        x.EditCountWithLinkedChange "InsertLineAbove" count (fun () ->

            // REPEAT TODO: Need to file a bug to get the caret position correct here for redo
            let line = x.CaretLine
            let newLineText = _commonOperations.GetNewLineText x.CaretPoint
            _textBuffer.Replace(new Span(line.Start.Position,0), newLineText) |> ignore

            // Position the caret for the edit
            let line = SnapshotUtil.GetLine x.CurrentSnapshot savedCaretLine.LineNumber
            x.MoveCaretToNewLineIndent savedCaretLine line)

    /// Insert a line below the current caret line and begin insert mode at the start of that
    /// line
    member x.InsertLineBelow count =

        // The caret position here odd.  The caret during undo / redo should be in the original
        // caret position.  However the edit needs to occur with the caret indented on the newly
        // created line.  So there are actually 3 caret positions to consider here
        //
        //  1. Before Edit (Undo)
        //  2. After the Edit but in the Transaction (Redo)
        //  3. For the eventual user edit

        let savedCaretLine = x.CaretLine
        let savedCaretPoint = x.CaretPoint

        // The the line below needs to be calculated agaist the visual snapshot.
        let visualLineEndIncludingLineBreak, newLineText, isLastLine =
            let visualSnapshotData = TextViewUtil.GetVisualSnapshotDataOrEdit _textView
            let newLineText = _commonOperations.GetNewLineText x.CaretPoint
            let isLastLine = SnapshotLineUtil.IsLastLine visualSnapshotData.CaretLine
            visualSnapshotData.CaretLine.EndIncludingLineBreak, newLineText, isLastLine

        x.EditCountWithLinkedChange "InsertLineBelow" count (fun () ->

            match BufferGraphUtil.MapPointDownToSnapshotStandard _bufferGraph visualLineEndIncludingLineBreak x.CurrentSnapshot with
            | None -> ()
            | Some point ->

                let span = SnapshotSpan(point, 0)
                _textBuffer.Replace(span.Span, newLineText) |> ignore

                TextViewUtil.MoveCaretToPosition _textView savedCaretPoint.Position

                let newLine =
                    let newPoint =
                        // When this command is run on the last line of the file then point will still
                        // refer to the original line.  In that case we need to move to the end of the
                        // ITextSnapshot
                        if isLastLine then
                            SnapshotUtil.GetEndPoint x.CurrentSnapshot
                        else
                            SnapshotPoint(x.CurrentSnapshot, point.Position)
                    SnapshotPointUtil.GetContainingLine newPoint
                x.MoveCaretToNewLineIndent savedCaretLine newLine)

    /// Jump to the next tag in the tag list
    member x.JumpToNewerPosition count =
        if not (_jumpList.MoveNewer count) then
            _commonOperations.Beep()
        else
            x.JumpToTagCore ()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Jump to the previous tag in the tag list
    member x.JumpToOlderPosition count =

        // Begin a traversal if we are no yet traversing
        if not _jumpList.IsTraversing then
            _jumpList.StartTraversal()

        if not (_jumpList.MoveOlder count) then
            _commonOperations.Beep()
        else
            x.JumpToTagCore ()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Jump to the specified mark.  Most motions are mapped to caret movements in MotionUtil.fs
    /// since they embed caret information.  Marks are special though because they have the ability
    /// to cross files hence we special case them here
    member x.JumpToMarkCore mark exact =
        let before = x.CaretPoint

        // If not exact, adjust point to first non-blank or start.
        let adjustPointForExact point =
            if exact then
                if not _globalSettings.IsVirtualEditOneMore
                    && not (SnapshotPointUtil.IsStartOfLine point)
                    && SnapshotPointUtil.IsInsideLineBreak point then
                    SnapshotPointUtil.GetPreviousCharacterSpanWithWrap point
                else
                    point
            else
                point
                |> SnapshotPointUtil.GetContainingLine
                |> SnapshotLineUtil.GetFirstNonBlankOrStart

        // Jump to the given point in the ITextBuffer
        let jumpLocal (point: VirtualSnapshotPoint) =
            let point = adjustPointForExact point.Position
            _commonOperations.MoveCaretToPoint point ViewFlags.Standard
            _jumpList.Add before
            CommandResult.Completed ModeSwitch.NoSwitch

        // Called when the mark is not set
        let markNotSet () =
            _statusUtil.OnError Resources.Common_MarkNotSet
            CommandResult.Error

        match mark with
        | Mark.GlobalMark letter ->
            let markMap = _vimTextBuffer.Vim.MarkMap
            match markMap.GetGlobalMark letter with
            | None ->

                // It's still possible that there is a global
                // mark, but the buffer has been unloaded.
                match markMap.GetMarkInfo mark _vimBufferData with
                | None -> markNotSet()
                | Some markInfo ->
                    let vimHost = _vimBufferData.Vim.VimHost
                    let name = markInfo.Name
                    let line = Some markInfo.Line
                    let column = if exact then Some markInfo.Column else None
                    if vimHost.LoadFileIntoNewWindow name line column then
                        CommandResult.Completed ModeSwitch.NoSwitch
                    else
                        _statusUtil.OnError (Resources.NormalMode_CantFindFile name)
                        CommandResult.Error
            | Some virtualPoint ->
                if virtualPoint.Position.Snapshot.TextBuffer = _textBuffer then
                    jumpLocal virtualPoint
                else
                    if
                        adjustPointForExact virtualPoint.Position
                        |> VirtualSnapshotPointUtil.OfPoint
                        |> _commonOperations.NavigateToPoint
                    then
                        _jumpList.Add before
                        CommandResult.Completed ModeSwitch.NoSwitch
                    else
                        _statusUtil.OnError Resources.Common_MarkNotSet
                        CommandResult.Error
        | Mark.LocalMark localMark ->
            match _vimTextBuffer.GetLocalMark localMark with
            | None -> markNotSet()
            | Some point -> jumpLocal point
        | Mark.LastJump ->
            match _jumpList.LastJumpLocation with
            | None -> markNotSet()
            | Some point -> jumpLocal point
        | Mark.LastExitedPosition ->
            match _vimTextBuffer.Vim.MarkMap.GetMark Mark.LastExitedPosition _vimBufferData with
            | None -> markNotSet()
            | Some point -> jumpLocal point

    /// Jump to the specified mark
    member x.JumpToMark mark =
        x.JumpToMarkCore mark true

    /// Jump to the start of the line containing the specified mark
    member x.JumpToMarkLine mark =
        x.JumpToMarkCore mark false

    /// Jumps to the specified
    member x.JumpToTagCore () =
        match _jumpList.Current with
        | None -> _commonOperations.Beep()
        | Some point -> _commonOperations.MoveCaretToPoint point ViewFlags.Standard

    /// Move the caret to start of a line which is deleted.  Needs to preserve the original
    /// indent if 'autoindent' is set.
    ///
    /// Be wary of using this function.  It has the implicit contract that the Start position
    /// of the line is still valid.
    member x.MoveCaretToDeletedLineStart (deletedLine: ITextSnapshotLine) =
        Contract.Requires (deletedLine.Start.Position <= x.CurrentSnapshot.Length)

        if _localSettings.AutoIndent then
            // Caret needs to be positioned at the indentation point of the previous line.  Don't
            // create actual whitespace, put the caret instead into virtual space
            let point =
                deletedLine.Start
                |> SnapshotPointUtil.GetContainingLine
                |> SnapshotLineUtil.GetIndentPoint

            // We are moving the caret into virtual space here.  Hence we need to do this in terms
            // of spaces and not absolute character column.  Basically we have to expand tabs to the
            // appropriate number of spaces
            let column = _commonOperations.GetSpacesToPoint point

            if column = 0 then
                TextViewUtil.MoveCaretToPosition _textView deletedLine.Start.Position
            else
                let point = SnapshotUtil.GetPoint x.CurrentSnapshot deletedLine.Start.Position
                let virtualPoint = VirtualSnapshotPoint(point, column)
                TextViewUtil.MoveCaretToVirtualPoint _textView virtualPoint
        else
            // Put the caret at column 0
            TextViewUtil.MoveCaretToPosition _textView deletedLine.Start.Position

    /// Move the caret to the proper indent on the newly created line
    member x.MoveCaretToNewLineIndent contextLine newLine =

        // Calling GetNewLineIndent can cause a buffer edit.  Need to rebind
        // the snapshot related items after calling the API
        let indent = _commonOperations.GetNewLineIndent contextLine newLine
        match SnapshotUtil.TryGetLine x.CurrentSnapshot newLine.LineNumber, indent with
        | Some newLine, Some indent ->
            let virtualPoint = VirtualSnapshotPoint(newLine.Start, indent)
            TextViewUtil.MoveCaretToVirtualPoint _textView virtualPoint
        | Some newLine, None ->
            TextViewUtil.MoveCaretToPoint _textView newLine.Start
        | None, Some _ -> ()
        | None, None -> ()

    /// The Join commands (Visual and Normal) have identical cursor positioning behavior and
    /// it's non-trivial so it's factored out to a function here.  In short the caret should be
    /// positioned 1 position after the last character in the second to last line of the join
    /// The caret should be positioned one after the second to last line in the
    /// join.  It should have it's original position during an undo so don't
    /// move the caret until we're inside the transaction
    member x.MoveCaretFollowingJoin (range: SnapshotLineRange) =
        let point =
            let number = range.StartLineNumber + range.Count - 2
            let line = SnapshotUtil.GetLine range.Snapshot number
            line |> SnapshotLineUtil.GetLastIncludedPoint |> OptionUtil.getOrDefault line.Start
        match TrackingPointUtil.GetPointInSnapshot point PointTrackingMode.Positive x.CurrentSnapshot with
        | None ->
            ()
        | Some point ->
            let point = SnapshotPointUtil.AddOneOrCurrent point
            TextViewUtil.MoveCaretToPoint _textView point

    /// Move the caret to the result of the motion
    member x.MoveCaretToMotion motion count =
        let argument = MotionArgument(MotionContext.Movement, operatorCount = None, motionCount = count)
        match _motionUtil.GetMotion motion argument with
        | None ->
            // If the motion couldn't be gotten then just beep
            _commonOperations.Beep()
            CommandResult.Error
        | Some result ->

            let point = x.CaretVirtualPoint
            _commonOperations.MoveCaretToMotionResult result

            if point = x.CaretVirtualPoint then
                // Failure to move the caret for a motion results in a beep for certain motions.  There
                // isn't any documentation here but experimentally it is true for 'l' and 'h'.
                //
                // Though attractive the correct solution is *not* to have the motion itself fail
                // in those cases.  While a 'h' movement fails when the caret is in column 0, a yank
                // from the same location, 'yh', succeeds.  It yanks nothing but it does so
                // successfully.  The error only happens when it's applied to a movement.
                let makeError () =
                    _commonOperations.Beep()
                    CommandResult.Error
                match motion with
                | Motion.CharLeft -> makeError()
                | Motion.CharRight -> makeError()
                | _ -> CommandResult.Completed ModeSwitch.NoSwitch
            else
                CommandResult.Completed ModeSwitch.NoSwitch

    /// Move the caret to the result of the text object selection
    member x.MoveCaretToTextObject count motion textObjectKind (visualSpan: VisualSpan) =

        // First step is to get the desired final mode of the text object movement
        let desiredVisualKind =
            match textObjectKind with
            | TextObjectKind.None -> visualSpan.VisualKind
            | TextObjectKind.AlwaysCharacter -> VisualKind.Character
            | TextObjectKind.AlwaysLine -> VisualKind.Line
            | TextObjectKind.LineToCharacter ->
                if _vimTextBuffer.ModeKind = ModeKind.VisualLine then
                    VisualKind.Character
                else
                    visualSpan.VisualKind

        let onError () =
            _commonOperations.Beep()
            CommandResult.Error

        let setSelection span =
            let visualSpan = VisualSpan.CreateForSpan span desiredVisualKind _localSettings.TabStop
            let visualSelection = VisualSelection.CreateForward visualSpan
            let argument = ModeArgument.InitialVisualSelection (visualSelection, None)
            x.SwitchMode desiredVisualKind.VisualModeKind argument

        let isInitialSelection =
            match visualSpan with
            | VisualSpan.Character characterSpan ->
                let lineBreakSpan = SnapshotLineUtil.GetLineBreakSpan characterSpan.LastLine
                characterSpan.Length <= 1 || characterSpan.Span = lineBreakSpan
            | VisualSpan.Block blockSpan -> blockSpan.Spaces <= 1
            | VisualSpan.Line lineRange -> lineRange.Count = 1

        let moveTag kind =
            if isInitialSelection then
                match _motionUtil.GetTextObject motion x.CaretPoint with
                | None -> onError()
                | Some motionResult -> setSelection motionResult.Span
            else
                match _motionUtil.GetExpandedTagBlock visualSpan.Start kind with
                | None -> onError()
                | Some span -> setSelection span

        // Handle the normal set of text objects (essentially non-block movements)
        let moveNormal () =

            // TODO: Backwards motions
            // TODO: The non-initial selection needs to ensure we're in the correct mode

            // The behavior of a text object depends highly on whether or not this is
            // visual mode in it's initial state.  The docs define this as the start
            // and end being the same but that's not true for line mode where stard and
            // end are rarely the same.
            if isInitialSelection then
                // For an initial selection we just do a standard motion from the caret point
                // and update the selection.
                let argument = MotionArgument(MotionContext.Movement, operatorCount = None, motionCount = Some count)
                match _motionUtil.GetMotion motion argument with
                | None -> onError ()
                | Some motionResult ->

                    // The initial selection span for a text object doesn't change based on
                    // whether the selection is inclusive / exclusive.  Only the caret position
                    // changes
                    setSelection motionResult.Span
            else
                // Need to move the caret to the next item.  When we are in inclusive selection
                // though the caret is at the last position of the previous motion so doing the
                // motion again is essentially a no-op.  Calculate the correct point from which
                // to do the motion
                let point =
                    match _globalSettings.SelectionKind with
                    | SelectionKind.Inclusive -> SnapshotPointUtil.AddOneOrCurrent x.CaretPoint
                    | SelectionKind.Exclusive -> x.CaretPoint

                match _motionUtil.GetTextObject motion point with
                | None -> onError()
                | Some motionResult ->
                    _commonOperations.MoveCaretToMotionResult motionResult
                    CommandResult.Completed ModeSwitch.NoSwitch

        let moveBlock blockKind motion =
            let argument = MotionArgument(MotionContext.Movement, operatorCount = None, motionCount = Some count)
            match _motionUtil.GetMotion motion argument with
            | None -> onError ()
            | Some motionResult ->
                let blockVisualSpan = VisualSpan.CreateForSpan motionResult.Span desiredVisualKind _localSettings.TabStop
                if motionResult.Span.Length = 0 || visualSpan <> blockVisualSpan then
                    // Selection is not yet the entire block so just expand to the block
                    setSelection motionResult.Span
                else
                    // Attempt to expand the selection to the encompassing block.  Simply move
                    // the caret outside the current block and attempt again to get the
                    let contextPoint =
                        let offset = match motion with
                                     | Motion.AllBlock _ -> 1
                                     | Motion.InnerBlock _ -> 2
                                     | _ -> 0

                        SnapshotPointUtil.TryAdd offset x.CaretPoint
                    match contextPoint |> OptionUtil.map2 (fun point -> _motionUtil.GetTextObject motion point) with
                    | None -> onError()
                    | Some motionResult -> setSelection motionResult.Span

        match motion with
        | Motion.AllBlock blockKind -> moveBlock blockKind motion
        | Motion.InnerBlock blockKind -> moveBlock blockKind motion
        | Motion.TagBlock kind -> moveTag kind
        | Motion.QuotedStringContents quote -> moveBlock quote motion
        | _ -> moveNormal ()

    /// Open a fold in visual mode.  In Visual Mode a single fold level is opened for every
    /// line in the selection
    member x.OpenFoldInSelection (visualSpan: VisualSpan) =
        let range = visualSpan.LineRange
        let offset = range.StartLineNumber
        for i = 0 to range.Count - 1 do
            let line = SnapshotUtil.GetLine x.CurrentSnapshot (offset + 1)
            _foldManager.OpenFold line.Start 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Toggle fold under the caret
    member x.ToggleFoldUnderCaret count =
        _foldManager.ToggleFold x.CaretPoint count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Toggle all folds in the buffer
    member x.ToggleAllFolds() =
        let span = SnapshotUtil.GetExtent x.CurrentSnapshot
        _foldManager.ToggleAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Open 'count' folds under the caret
    member x.OpenFoldUnderCaret count =
        _foldManager.OpenFold x.CaretPoint count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Open all of the folds in the buffer
    member x.OpenAllFolds() =
        let span = SnapshotUtil.GetExtent x.CurrentSnapshot
        _foldManager.OpenAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Open all of the folds under the caret
    member x.OpenAllFoldsUnderCaret () =
        let span = SnapshotSpan(x.CaretPoint, 1)
        _foldManager.OpenAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Open all folds under the caret in visual mode
    member x.OpenAllFoldsInSelection (visualSpan: VisualSpan) =
        let span = visualSpan.LineRange.ExtentIncludingLineBreak
        _foldManager.OpenAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Run the Ping command
    member x.Ping (pingData: PingData) data =
        pingData.Function data

    /// Put the contents of the specified register after the cursor.  Used for the
    /// 'p' and 'gp' command in normal mode
    member x.PutAfterCaret registerName count moveCaretAfterText =
        let register = x.GetRegister registerName
        x.EditWithUndoTransaction "Put after" (fun () ->
            x.PutAfterCaretCore (register.RegisterValue) count moveCaretAfterText)
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Core put after function used by many of the put after operations
    member x.PutAfterCaretCore (registerValue: RegisterValue) count moveCaretAfterText =
        let stringData = registerValue.StringData.ApplyCount count

        // Adjust for simple putting line-wise "after" in an empty buffer.
        // This is just the way vim works and cannot be handled later
        // because the behavior depends not on the point but the "after"
        // part, which is lost by the time we get to 'PutCore' and beyond
        let stringData =
            let isLineWise = registerValue.OperationKind = OperationKind.LineWise
            let isEmpty = _textBuffer.CurrentSnapshot.Length = 0
            let isSimple =
                match stringData with
                | StringData.Simple _ -> true
                | _ -> false
            if isLineWise && isEmpty && isSimple then
                let newLine = _commonOperations.GetNewLineText x.CaretPoint
                let newString = newLine + (EditUtil.RemoveEndingNewLine stringData.String)
                StringData.Simple newString
            else
                stringData

        let point =
            match registerValue.OperationKind with
            | OperationKind.CharacterWise ->
                _commonOperations.FillInVirtualSpace()
                if x.CaretLine.Length = 0 then
                    x.CaretLine.Start
                elif SnapshotPointUtil.IsInsideLineBreak x.CaretPoint then
                    x.CaretLine.End
                else
                    SnapshotPointUtil.AddOneOrCurrent x.CaretPoint
            | OperationKind.LineWise ->
                x.CaretLine.EndIncludingLineBreak

        x.PutCore point stringData registerValue.OperationKind moveCaretAfterText false

    /// Put the contents of the register into the buffer after the cursor and respect
    /// the indent of the current line.  Used for the ']p' command
    member x.PutAfterCaretWithIndent registerName count =
        let register = x.GetRegister registerName
        let registerValue = x.CalculateIdentStringData register.RegisterValue
        x.EditWithUndoTransaction "Put after with indent" (fun () ->
            x.PutAfterCaretCore registerValue count false)
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Happens when the middle mouse button is clicked.  Need to paste the contents of the default
    /// register at the current position
    member x.PutAfterCaretMouse() =
        match TextViewUtil.GetTextViewLines _textView with
        | None -> ()
        | Some textViewLines ->
            match _mouseDevice.GetPosition _textView with
            | None -> ()
            | Some position ->

                // First move the caret to the current mouse position
                let textViewLine = textViewLines.GetTextViewLineContainingYCoordinate(position.Y + _textView.ViewportTop)
                _textView.Caret.MoveTo(textViewLine, position.X + _textView.ViewportLeft) |> ignore

                // Now run the put after command
                let register = x.GetRegister (Some RegisterName.Unnamed)
                x.EditWithUndoTransaction "Put after mouse" (fun () ->
                    x.PutAfterCaretCore register.RegisterValue 1 false)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Put the contents of the specified register before the cursor.  Used for the
    /// 'P' and 'gP' commands in normal mode
    member x.PutBeforeCaret registerName count moveCaretAfterText =
        let register = x.GetRegister registerName
        x.EditWithUndoTransaction "Put before" (fun () ->
            x.PutBeforeCaretCore register.RegisterValue count moveCaretAfterText)
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Put the contents of the specified register before the caret and respect the
    /// indent of the current line.  Used for the '[p' and family commands
    member x.PutBeforeCaretWithIndent registerName count =
        let register = x.GetRegister registerName
        let registerValue = x.CalculateIdentStringData register.RegisterValue
        x.EditWithUndoTransaction "Put before with indent" (fun () ->
            x.PutBeforeCaretCore registerValue count false)
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Core put function used by many of the put before operations
    member x.PutBeforeCaretCore (registerValue: RegisterValue) count moveCaretAfterText =
        let stringData = registerValue.StringData.ApplyCount count
        let point =
            match registerValue.OperationKind with
            | OperationKind.CharacterWise ->
                _commonOperations.FillInVirtualSpace()
                x.CaretPoint
            | OperationKind.LineWise -> x.CaretLine.Start

        x.PutCore point stringData registerValue.OperationKind moveCaretAfterText false

    /// Put the contents of the specified register after the cursor.  Used for the
    /// normal 'p', 'gp', 'P', 'gP', ']p' and '[p' commands.  For linewise put operations
    /// the point must be at the start of a line
    member x.PutCore point stringData operationKind moveCaretAfterText moveCaretAsIfSimple =

        // Save the point incase this is a linewise insertion and we need to
        // move after the inserted lines
        let oldPoint = point

        // The caret should be positioned at the current position in undo so don't move
        // it before the transaction.
        x.EditWithUndoTransaction "Put" (fun () ->

            _commonOperations.Put point stringData operationKind

            // Edit is complete.  Position the caret against the updated text.  First though
            // get the original insertion point in the new ITextSnapshot
            let point = SnapshotUtil.GetPoint x.CurrentSnapshot point.Position
            match operationKind with
            | OperationKind.CharacterWise ->

                let point =
                    match stringData, moveCaretAsIfSimple with
                    | StringData.Simple _, true
                    | StringData.Simple _, false
                    | StringData.Block _, true ->
                        let text = stringData.FirstString
                        if EditUtil.HasNewLine text && not moveCaretAfterText then
                            // For multi-line operations which do not specify to move the caret after
                            // the text we instead put the caret at the first character of the new
                            // text
                            point
                        else
                            // For characterwise we just increment the length of the first string inserted
                            // and possibily one more if moving after
                            let offset = text.Length - 1
                            let offset = max 0 offset
                            let point = SnapshotPointUtil.Add offset point
                            if moveCaretAfterText then SnapshotPointUtil.AddOneOrCurrent point else point
                    | StringData.Block col, false ->
                        if moveCaretAfterText then
                            // Needs to be positioned after the last item in the collection
                            let line =
                                let number = oldPoint |> SnapshotPointUtil.GetContainingLine |> SnapshotLineUtil.GetLineNumber
                                let number = number + (col.Count - 1)
                                SnapshotUtil.GetLine x.CurrentSnapshot number
                            let offset = (SnapshotPointUtil.GetColumn point) + col.Head.Length
                            SnapshotPointUtil.Add offset line.Start
                        else
                            // Position at the original insertion point
                            SnapshotUtil.GetPoint x.CurrentSnapshot oldPoint.Position

                _commonOperations.MoveCaretToPoint point ViewFlags.VirtualEdit
            | OperationKind.LineWise ->

                // Get the line on which we will be positioning the caret
                let line =
                    if moveCaretAfterText then
                        // Move to the first line after the insertion.  Can be calculated with a line
                        // count offset
                        let offset = x.CurrentSnapshot.LineCount - oldPoint.Snapshot.LineCount
                        let number = oldPoint |> SnapshotPointUtil.GetContainingLine |> SnapshotLineUtil.GetLineNumber
                        SnapshotUtil.GetLine x.CurrentSnapshot (number + offset)
                    else
                        // The caret should be moved to the first line of the inserted text.
                        let number =
                            let oldLineNumber = oldPoint |> SnapshotPointUtil.GetContainingLine |> SnapshotLineUtil.GetLineNumber
                            if SnapshotPointUtil.IsStartOfLine oldPoint then
                                oldLineNumber
                            else
                                // Anything other than the start of the line will cause the Put to
                                // occur one line below and we need to account for that
                                oldLineNumber + 1
                        SnapshotUtil.GetLine x.CurrentSnapshot number

                // Get the indent point of the line.  That's what the caret needs to be moved to
                let point = SnapshotLineUtil.GetIndentPoint line
                _commonOperations.MoveCaretToPoint point ViewFlags.VirtualEdit)

    /// Put the contents of the specified register over the selection.  This is used for all
    /// visual mode put commands.
    member x.PutOverSelection registerName count moveCaretAfterText visualSpan =

        // Build up the common variables
        let register =x .GetRegister registerName
        let stringData = register.StringData.ApplyCount count
        let operationKind = register.OperationKind

        let deletedSpan, operationKind =
            match visualSpan with
            | VisualSpan.Character characterSpan ->

                // Cursor needs to be at the start of the span during undo and at the end
                // of the pasted span after redo so move to the start before the undo transaction
                TextViewUtil.MoveCaretToPoint _textView characterSpan.Start
                x.EditWithUndoTransaction "Put" (fun () ->

                    // Delete the span and move the caret back to the start of the
                    // span in the new ITextSnapshot
                    _textBuffer.Delete(characterSpan.Span.Span) |> ignore
                    TextViewUtil.MoveCaretToPosition _textView characterSpan.Start.Position

                    // Now do a standard put operation at the original start point in the current
                    // ITextSnapshot
                    let point = SnapshotUtil.GetPoint x.CurrentSnapshot characterSpan.Start.Position
                    x.PutCore point stringData operationKind moveCaretAfterText false

                    EditSpan.Single characterSpan.Span, OperationKind.CharacterWise)
            | VisualSpan.Line range ->

                // Cursor needs to be positioned at the start of the range for both undo so
                // move the caret now
                TextViewUtil.MoveCaretToPoint _textView range.Start
                x.EditWithUndoTransaction "Put" (fun () ->

                    // When putting over a linewise selection the put needs to be done
                    // in a linewise fashion.  This means in certain cases we have to adjust
                    // the StringData to have proper newline semantics
                    let stringData =
                        match stringData with
                        | StringData.Simple str ->
                            let str = if EditUtil.EndsWithNewLine str then str else str + (EditUtil.NewLine _options)
                            StringData.Simple str
                        | StringData.Block _ ->
                            stringData
                    let operationKind = OperationKind.LineWise

                    // Delete the span and move the caret back to the start
                    _textBuffer.Delete(range.ExtentIncludingLineBreak.Span) |> ignore
                    TextViewUtil.MoveCaretToPosition _textView range.Start.Position

                    // Now do a standard put operation at the start of the SnapshotLineRange
                    // in the current ITextSnapshot
                    let point = SnapshotUtil.GetPoint x.CurrentSnapshot range.Start.Position
                    x.PutCore point stringData operationKind moveCaretAfterText false

                    EditSpan.Single range.ExtentIncludingLineBreak, OperationKind.LineWise)

            | VisualSpan.Block blockSpan ->

                // Cursor needs to be positioned at the start of the range for undo so
                // move the caret now
                let col = blockSpan.BlockOverlapSpans
                let span = col.Head
                TextViewUtil.MoveCaretToPoint _textView span.Start.Point
                x.EditWithUndoTransaction "Put" (fun () ->

                    // Delete all of the items in the collection
                    use edit = _textBuffer.CreateEdit()
                    col |> Seq.iter (fun span -> edit.Delete(span) |> ignore)
                    edit.Apply() |> ignore

                    // Caret position depends on whether the original string data was simple.
                    let moveCaretAsIfSimple =
                        match stringData with
                        | StringData.Simple _ -> true
                        | StringData.Block _ -> false

                    // For character-wise put of simple text over a
                    // block selection, replicate the text into a block.
                    let stringData =
                        match operationKind, stringData with
                        | OperationKind.CharacterWise, StringData.Simple text ->
                            col
                            |> NonEmptyCollectionUtil.Map (fun _ -> text)
                            |> StringData.Block
                        | _ ->
                            stringData

                    // Now do a standard put operation.  The point of the put varies a bit
                    // based on whether we're doing a linewise or characterwise insert
                    let point =
                        match operationKind with
                        | OperationKind.CharacterWise ->
                            // Put occurs at the start of the original span
                            SnapshotUtil.GetPoint x.CurrentSnapshot span.Start.Point.Position
                        | OperationKind.LineWise ->
                            // Put occurs on the line after the last edit
                            let lastSpan = col |> SeqUtil.last
                            let number = lastSpan.Start.Point |> SnapshotPointUtil.GetContainingLine |> SnapshotLineUtil.GetLineNumber
                            SnapshotUtil.GetLine x.CurrentSnapshot number |> SnapshotLineUtil.GetEndIncludingLineBreak
                    x.PutCore point stringData operationKind moveCaretAfterText moveCaretAsIfSimple

                    EditSpan.Block col, OperationKind.CharacterWise)

        // Update the unnamed register with the deleted text
        let value = x.CreateRegisterValue (StringData.OfEditSpan deletedSpan) operationKind
        _commonOperations.SetRegisterValue (Some RegisterName.Unnamed) RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    member x.PrintFileInformation() =
        let filePath = _vimHost.GetName _textBuffer
        let snapshot = _textBuffer.CurrentSnapshot
        let lineCount = SnapshotUtil.GetNormalizedLineCountExcludingEmpty snapshot
        let percent = if lineCount = 0 then 0 else x.CaretLineNumber * 100 / lineCount
        let msg = sprintf "%s %d lines --%d%%--" filePath lineCount percent
        _statusUtil.OnStatus msg
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Start a macro recording
    member x.RecordMacroStart c =
        let isAppend, c =
            if CharUtil.IsUpper c && CharUtil.IsLetter c then
                true, CharUtil.ToLower c
            else
                false, c

        // Restrict the register to the valid ones for macros
        let name =
            if CharUtil.IsLetter c then
                NamedRegister.OfChar c |> Option.map RegisterName.Named
            elif CharUtil.IsDigit c then
                NumberedRegister.OfChar c |> Option.map RegisterName.Numbered
            elif c = '"' then
                RegisterName.Unnamed |> Some
            else
                None

        match name with
        | None ->
            // Beep on an invalid macro register
            _commonOperations.Beep()
        | Some name ->
            let register = _registerMap.GetRegister name
            _macroRecorder.StartRecording register isAppend

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Stop a macro recording
    member x.RecordMacroStop () =
        _macroRecorder.StopRecording()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Redo count operations in the ITextBuffer
    member x.Redo count =
        use guard = new NormalModeSelectionGuard(_vimBufferData)
        _commonOperations.Redo count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Repeat the last executed command against the current buffer
    member x.RepeatLastCommand (repeatData: CommandData) =

        // Chain the running of the next command on the basis of the success of
        // the previous command
        let chainCommand commandResult runNextCommand =
            match commandResult with
            | CommandResult.Error ->
                commandResult
            | CommandResult.Completed modeSwitch ->
                match modeSwitch with
                | ModeSwitch.SwitchModeWithArgument (_, modeArgument) ->
                    modeArgument.CompleteAnyTransaction
                | _ -> ()
                runNextCommand ()

        // Repeating an insert command is a bit different than repeating a normal command because
        // of the way the caret position is handled.  Every insert command ends with a move left
        // on the caret.  When repeating this move left is only done once though.
        let repeatInsert command count =
            let command, doMoveLeft =
                match command with
                | InsertCommand.Combined (leftCommand, rightCommand) ->
                    match leftCommand with
                    | InsertCommand.MoveCaret Direction.Left -> rightCommand, true
                    | _ -> command, false
                | _ -> command, false

            // Run the commands in sequence.  Only continue onto the second if the first
            // command succeeds.  We do want any actions performed in the linked commands
            // to remain linked so do this inside of an edit transaction
            let rec func count commandResult =
                if count = 0 then
                    commandResult
                else
                    chainCommand commandResult (fun () -> func (count - 1) (_insertUtil.RunInsertCommand command))

            let commandResult = func (count - 1) (_insertUtil.RunInsertCommand command)

            if doMoveLeft then
                chainCommand commandResult (fun () -> _insertUtil.RunInsertCommand (InsertCommand.MoveCaret Direction.Left))
            else
                commandResult

        // Function to actually repeat the last change
        let rec repeat (storedCommand: StoredCommand) (repeatData: CommandData option) =

            // Before repeating a command it needs to be updated in the context of the repeat operation. This
            // includes recalculating the visual span and considering explicit counts that are passed into
            // the repeat operation.
            //
            // When a count is passed to repeat then it acts as if it was the only count passed to the original
            // command. This overrides the original count or counts passed to motion operators.
            let repeatCount =
                match repeatData with
                | None -> None
                | Some r -> r.Count

            match storedCommand with
            | StoredCommand.NormalCommand (command, data, flags) ->
                let command, data =
                    match repeatCount with
                    | Some _ ->
                        let data = { data with Count = repeatCount }
                        let command = command.ChangeMotionData (fun motionData ->
                            let argument = MotionArgument(motionData.MotionArgument.MotionContext, operatorCount = None, motionCount = repeatCount)
                            { motionData with MotionArgument = argument })
                        (command, data)
                    | None -> (command, data)
                let data =
                    match command with
                    | NormalCommand.PutAfterCaret _
                    | NormalCommand.PutBeforeCaret _
                    | NormalCommand.PutAfterCaretWithIndent
                    | NormalCommand.PutBeforeCaretWithIndent
                        ->

                        // Special case when redoing positive numbered register puts:
                        // increment the register number (see vim ':help redo-register').
                        match data.RegisterName with
                        | Some (RegisterName.Numbered numberedRegister) ->
                            match numberedRegister.NextPositive with
                            | Some nextNumberedRegister ->
                                let nextRegister = RegisterName.Numbered nextNumberedRegister
                                let newData = { data with RegisterName = Some nextRegister }
                                let newStoredCommand = StoredCommand.NormalCommand (command, newData, flags)
                                _vimData.LastCommand <- Some newStoredCommand
                                newData
                            | _ ->
                                data
                        | _ ->
                            data
                    | _ ->
                        data
                x.RunNormalCommand command data
            | StoredCommand.VisualCommand (command, data, storedVisualSpan, _) ->
                let data =
                    match repeatCount with
                    | Some _ -> { data with Count = repeatCount }
                    | None -> data
                let visualSpan = x.CalculateVisualSpan storedVisualSpan
                x.RunVisualCommand command data visualSpan
            | StoredCommand.InsertCommand (command, _) ->

                let count =
                    match repeatData with
                    | Some repeatData -> repeatData.CountOrDefault
                    | None -> 1

                repeatInsert command count
            | StoredCommand.LinkedCommand (command1, command2) ->

                // Run the commands in sequence.  Only continue onto the second if the first
                // command succeeds.  We do want any actions performed in the linked commands
                // to remain linked so do this inside of an edit transaction
                let commandResult = repeat command1 repeatData
                chainCommand commandResult (fun () -> repeat command2 None)

        if _inRepeatLastChange then
            _statusUtil.OnError Resources.NormalMode_RecursiveRepeatDetected
            CommandResult.Error
        else
            use bulkOperation = _bulkOperations.BeginBulkOperation()
            try
                _inRepeatLastChange <- true
                match _vimData.LastCommand with
                | None ->
                    _commonOperations.Beep()
                    CommandResult.Completed ModeSwitch.NoSwitch
                | Some command ->
                    use transaction = _undoRedoOperations.CreateLinkedUndoTransactionWithFlags "Repeat Command" LinkedUndoTransactionFlags.CanBeEmpty
                    let result = repeat command (Some repeatData)
                    transaction.Complete()
                    result

            finally
                _inRepeatLastChange <- false

    /// Repeat the last substitute command.
    member x.RepeatLastSubstitute useSameFlags =
        match _vimData.LastSubstituteData with
        | None -> _commonOperations.Beep()
        | Some data ->
            let range = SnapshotLineRangeUtil.CreateForLine x.CaretLine
            let flags =
                if useSameFlags then
                    data.Flags
                else
                    SubstituteFlags.None
            _commonOperations.Substitute data.SearchPattern data.Substitute range flags

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Replace the text at the caret via replace mode
    member x.ReplaceAtCaret count =
        let switch = ModeSwitch.SwitchModeWithArgument (ModeKind.Replace, ModeArgument.InsertWithCount count)
        CommandResult.Completed switch

    /// Replace the char under the cursor with the specified character
    member x.ReplaceChar keyInput count =

        let point = x.CaretPoint
        let line = point.GetContainingLine()

        let replaceChar () =
            let span = new Span(point.Position, count)
            let position =
                if keyInput = KeyInputUtil.EnterKey then
                    // Special case for replacement with a newline.  First, vim only inserts a
                    // single newline regardless of the count.  Second, let the host do any magic
                    // by simulating a keystroke, e.g. from inside a C# documentation comment.
                    // The caret goes one character to the left of whereever it ended up
                    _textBuffer.Delete(span) |> ignore
                    _insertUtil.RunInsertCommand(InsertCommand.InsertNewLine) |> ignore
                    let caretPoint = TextViewUtil.GetCaretPoint _textView
                    caretPoint.Position - 1
                else
                    // The caret should move to the end of the replace operation which is
                    // 'count - 1' characters from the original position
                    let replaceText = new System.String(keyInput.Char, count)
                    _textBuffer.Replace(span, replaceText) |> ignore
                    point.Position + count - 1

            // Don't use the ITextSnapshot that is returned from Replace.  This represents the ITextSnapshot
            // after our change.  If other components are listening to the Change events they could make their
            // own change.  The ITextSnapshot returned reflects only our change, not theirs.  To properly
            // position the caret we need the current ITextSnapshot
            let snapshot = _textBuffer.CurrentSnapshot

            // It's possible for any edit to occur after ours including the complete deletion of the buffer
            // contents.  Need to account for this in the caret positioning.
            let position = min position snapshot.Length
            let point = SnapshotPoint(snapshot, position)
            let point =
                if SnapshotPointUtil.IsInsideLineBreak point then
                    SnapshotPointUtil.GetNextPointWithWrap point
                else
                    point
            TextViewUtil.MoveCaretToPoint _textView point

        // If the replace operation exceeds the line length then the operation
        // can't succeed
        if (point.Position + count) > line.End.Position then
            // If the replace failed then we should beep the console
            _commonOperations.Beep()
            CommandResult.Error
        else
            // Do the replace in an undo transaction since we are explicitly positioning
            // the caret
            x.EditWithUndoTransaction "ReplaceChar" (fun () -> replaceChar())
            CommandResult.Completed ModeSwitch.NoSwitch

    /// Replace the char under the cursor in visual mode.
    member x.ReplaceSelection keyInput (visualSpan: VisualSpan) =

        let replaceText =
            if keyInput = KeyInputUtil.EnterKey then EditUtil.NewLine _options
            else System.String(keyInput.Char, 1)

        // First step is we want to update the selection.  A replace char operation
        // in visual mode should position the caret on the first character and clear
        // the selection (both before and after).
        //
        // The caret can be anywhere at the start of the operation so move it to the
        // first point before even beginning the edit transaction
        _textView.Selection.Clear()
        TextViewUtil.MoveCaretToPoint _textView visualSpan.Start

        x.EditWithUndoTransaction "ReplaceChar" (fun () ->
            use edit = _textBuffer.CreateEdit()
            let builder = System.Text.StringBuilder()

            for span in visualSpan.OverlapSpans do
                if span.HasOverlapStart then
                    let startPoint = span.Start
                    builder.Length <- 0
                    builder.AppendCharCount ' ' startPoint.SpacesBefore
                    builder.AppendStringCount replaceText (startPoint.Spaces - startPoint.SpacesBefore)
                    edit.Replace(Span(startPoint.Point.Position, 1), (builder.ToString())) |> ignore

                SnapshotSpanUtil.GetPoints SearchPath.Forward span.InnerSpan
                |> Seq.filter (fun point -> not (SnapshotPointUtil.IsInsideLineBreak point))
                |> Seq.iter (fun point -> edit.Replace(Span(point.Position, 1), replaceText) |> ignore)

            // Reposition the caret at the start of the edit
            let editPoint = x.ApplyEditAndMapPoint edit visualSpan.Start.Position
            TextViewUtil.MoveCaretToPoint _textView editPoint)

        CommandResult.Completed (ModeSwitch.SwitchMode ModeKind.Normal)

    /// Run the specified Command
    member x.RunCommand command =
        match command with
        | Command.NormalCommand (command, data) -> x.RunNormalCommand command data
        | Command.VisualCommand (command, data, visualSpan) -> x.RunVisualCommand command data visualSpan
        | Command.InsertCommand command -> x.RunInsertCommand command

    /// Run an 'at' command for the specified character
    member x.RunAtCommand char count =
        match char with
        | ':' ->

            // Repeat the last line command.
            let vim = _vimBufferData.Vim
            match vim.VimData.LastLineCommand with
            | None ->
                _commonOperations.Beep()
            | Some lastLineCommand ->
                match vim.GetVimBuffer _textView with
                | None -> _commonOperations.Beep()
                | Some vimBuffer ->
                    let vimInterpreter = vim.GetVimInterpreter vimBuffer
                    for i = 1 to count do
                        vimInterpreter.RunLineCommand lastLineCommand

        | '@' ->

            // Repeat the last macro.
            match _vimData.LastMacroRun with
            | None ->
                _commonOperations.Beep()
            | Some registerName ->
                x.RunMacro registerName count

        | registerName ->

            // Run the macro with the specified register name.
            x.RunMacro registerName count

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Run a macro using the contents of the specified register
    member x.RunMacro registerName count =

        let name =
            // TODO:  Need to handle, = and .
            if CharUtil.IsDigit registerName then
                NumberedRegister.OfChar registerName |> Option.map RegisterName.Numbered
            elif registerName = '*' then
                SelectionAndDropRegister.Star |> RegisterName.SelectionAndDrop |> Some
            else
                let registerName = CharUtil.ToLower registerName
                NamedRegister.OfChar registerName |> Option.map RegisterName.Named

        match name with
        | None ->
            _commonOperations.Beep()
        | Some name ->
            let register = _registerMap.GetRegister name
            let list = register.RegisterValue.KeyInputs

            // The macro should be executed as a single action and the macro can execute in
            // several ITextBuffer instances (consider if the macros executes a 'gt' and keeps
            // editing).  We need to have proper transactions for every ITextBuffer this macro
            // runs in
            //
            // Using .Net dictionary because we have to map by ITextBuffer which doesn't have
            // the comparison constraint
            let map = System.Collections.Generic.Dictionary<ITextBuffer, ILinkedUndoTransaction>();

            use bulkOperation = _bulkOperations.BeginBulkOperation()
            try

                // Actually run the macro by replaying the key strokes one at a time.  Returns
                // false if the macro should be stopped due to a failed command
                let runMacro () =
                    let rec inner list =
                        match list with
                        | [] ->
                            // No more input so we are finished
                            true
                        | keyInput :: tail ->

                            // Prefer the focussed IVimBuffer over the current.  It's possible for the
                            // macro playback switch the active buffer via gt, gT, etc ... and playback
                            // should continue on the newly focussed IVimBuffer.  Should the host API
                            // fail to return an active IVimBuffer continue using the original one
                            let buffer =
                                match _vim.FocusedBuffer with
                                | Some buffer -> Some buffer
                                | None -> _vim.GetVimBuffer _textView

                            match buffer with
                            | None ->
                                // Nothing to do if we don't have an ITextBuffer with focus
                                false
                            | Some buffer ->
                                // Make sure we have an IUndoTransaction open in the ITextBuffer
                                if not (map.ContainsKey(buffer.TextBuffer)) then
                                    let transaction = _undoRedoOperations.CreateLinkedUndoTransactionWithFlags "Macro Run" LinkedUndoTransactionFlags.CanBeEmpty
                                    map.Add(buffer.TextBuffer, transaction)

                                // Actually run the KeyInput.  If processing the KeyInput value results
                                // in an error then we should stop processing the macro
                                match buffer.Process keyInput with
                                | ProcessResult.Handled _ -> inner tail
                                | ProcessResult.HandledNeedMoreInput -> inner tail
                                | ProcessResult.NotHandled -> false
                                | ProcessResult.Error -> false

                    inner list

                // Run the macro count times.
                let go = ref true
                for i = 1 to count do
                    if go.Value then
                        go := runMacro()

                // Close out all of the transactions
                for transaction in map.Values do
                    transaction.Complete()

            finally

                // Make sure to dispose the transactions in a finally block.  Leaving them open
                // completely breaks undo in the ITextBuffer
                map.Values |> Seq.iter (fun transaction -> transaction.Dispose())

            _vimData.LastMacroRun <- Some registerName

    /// Run a InsertCommand against the buffer
    member x.RunInsertCommand command =
        _insertUtil.RunInsertCommand command

    /// Run a NormalCommand against the buffer
    member x.RunNormalCommand command (data: CommandData) =
        let registerName = data.RegisterName
        let count = data.CountOrDefault
        match command with
        | NormalCommand.AddToWord -> x.AddToWord count
        | NormalCommand.ChangeMotion motion -> x.RunWithMotion motion (x.ChangeMotion registerName)
        | NormalCommand.ChangeCaseCaretLine kind -> x.ChangeCaseCaretLine kind
        | NormalCommand.ChangeCaseCaretPoint kind -> x.ChangeCaseCaretPoint kind count
        | NormalCommand.ChangeCaseMotion (kind, motion) -> x.RunWithMotion motion (x.ChangeCaseMotion kind)
        | NormalCommand.ChangeLines -> x.ChangeLines count registerName
        | NormalCommand.ChangeTillEndOfLine -> x.ChangeTillEndOfLine count registerName
        | NormalCommand.CloseAllFolds -> x.CloseAllFolds()
        | NormalCommand.CloseAllFoldsUnderCaret -> x.CloseAllFoldsUnderCaret()
        | NormalCommand.CloseBuffer -> x.CloseBuffer()
        | NormalCommand.CloseWindow -> x.CloseWindow()
        | NormalCommand.CloseFoldUnderCaret -> x.CloseFoldUnderCaret count
        | NormalCommand.DeleteAllFoldsInBuffer -> x.DeleteAllFoldsInBuffer()
        | NormalCommand.DeleteAllFoldsUnderCaret -> x.DeleteAllFoldsUnderCaret()
        | NormalCommand.DeleteCharacterAtCaret -> x.DeleteCharacterAtCaret count registerName
        | NormalCommand.DeleteCharacterBeforeCaret -> x.DeleteCharacterBeforeCaret count registerName
        | NormalCommand.DeleteFoldUnderCaret -> x.DeleteFoldUnderCaret()
        | NormalCommand.DeleteLines -> x.DeleteLines count registerName
        | NormalCommand.DeleteMotion motion -> x.RunWithMotion motion (x.DeleteMotion registerName)
        | NormalCommand.DeleteTillEndOfLine -> x.DeleteTillEndOfLine count registerName
        | NormalCommand.DisplayCharacterBytes -> x.DisplayCharacterBytes()
        | NormalCommand.DisplayCharacterCodePoint -> x.DisplayCharacterCodePoint()
        | NormalCommand.FilterLines -> x.FilterLines count
        | NormalCommand.FilterMotion motion -> x.RunWithMotion motion x.FilterMotion
        | NormalCommand.FoldLines -> x.FoldLines data.CountOrDefault
        | NormalCommand.FoldMotion motion -> x.RunWithMotion motion x.FoldMotion
        | NormalCommand.FormatLines -> x.FormatLines count
        | NormalCommand.FormatMotion motion -> x.RunWithMotion motion x.FormatMotion
        | NormalCommand.GoToDefinition -> x.GoToDefinition()
        | NormalCommand.GoToFileUnderCaret useNewWindow -> x.GoToFileUnderCaret useNewWindow
        | NormalCommand.GoToGlobalDeclaration -> x.GoToGlobalDeclaration()
        | NormalCommand.GoToLocalDeclaration -> x.GoToLocalDeclaration()
        | NormalCommand.GoToNextTab path -> x.GoToNextTab path data.Count
        | NormalCommand.GoToWindow direction -> x.GoToWindow direction count
        | NormalCommand.GoToRecentView -> x.GoToRecentView count
        | NormalCommand.InsertAfterCaret -> x.InsertAfterCaret count
        | NormalCommand.InsertBeforeCaret -> x.InsertBeforeCaret count
        | NormalCommand.InsertAtEndOfLine -> x.InsertAtEndOfLine count
        | NormalCommand.InsertAtFirstNonBlank -> x.InsertAtFirstNonBlank count
        | NormalCommand.InsertAtStartOfLine -> x.InsertAtStartOfLine count
        | NormalCommand.InsertLineAbove -> x.InsertLineAbove count
        | NormalCommand.InsertLineBelow -> x.InsertLineBelow count
        | NormalCommand.JoinLines kind -> x.JoinLines kind count
        | NormalCommand.JumpToMark c -> x.JumpToMark c
        | NormalCommand.JumpToMarkLine c -> x.JumpToMarkLine c
        | NormalCommand.JumpToOlderPosition -> x.JumpToOlderPosition count
        | NormalCommand.JumpToNewerPosition -> x.JumpToNewerPosition count
        | NormalCommand.MoveCaretToMotion motion -> x.MoveCaretToMotion motion data.Count
        | NormalCommand.OpenAllFolds -> x.OpenAllFolds()
        | NormalCommand.OpenAllFoldsUnderCaret -> x.OpenAllFoldsUnderCaret()
        | NormalCommand.OpenFoldUnderCaret -> x.OpenFoldUnderCaret data.CountOrDefault
        | NormalCommand.Ping pingData -> x.Ping pingData data
        | NormalCommand.PutAfterCaret moveCaretAfterText -> x.PutAfterCaret registerName count moveCaretAfterText
        | NormalCommand.PutAfterCaretWithIndent -> x.PutAfterCaretWithIndent registerName count
        | NormalCommand.PutAfterCaretMouse -> x.PutAfterCaretMouse()
        | NormalCommand.PutBeforeCaret moveCaretBeforeText -> x.PutBeforeCaret registerName count moveCaretBeforeText
        | NormalCommand.PutBeforeCaretWithIndent -> x.PutBeforeCaretWithIndent registerName count
        | NormalCommand.PrintFileInformation -> x.PrintFileInformation()
        | NormalCommand.RecordMacroStart c -> x.RecordMacroStart c
        | NormalCommand.RecordMacroStop -> x.RecordMacroStop()
        | NormalCommand.Redo -> x.Redo count
        | NormalCommand.RepeatLastCommand -> x.RepeatLastCommand data
        | NormalCommand.RepeatLastSubstitute useSameFlags -> x.RepeatLastSubstitute useSameFlags
        | NormalCommand.ReplaceAtCaret -> x.ReplaceAtCaret count
        | NormalCommand.ReplaceChar keyInput -> x.ReplaceChar keyInput data.CountOrDefault
        | NormalCommand.RunAtCommand char -> x.RunAtCommand char data.CountOrDefault
        | NormalCommand.SetMarkToCaret c -> x.SetMarkToCaret c
        | NormalCommand.ScrollLines (direction, useScrollOption) -> x.ScrollLines direction useScrollOption data.Count
        | NormalCommand.ScrollPages direction -> x.ScrollPages direction data.CountOrDefault
        | NormalCommand.ScrollWindow direction -> x.ScrollWindow direction count
        | NormalCommand.ScrollCaretLineToTop keepCaretColumn -> x.ScrollCaretLineToTop keepCaretColumn
        | NormalCommand.ScrollCaretLineToMiddle keepCaretColumn -> x.ScrollCaretLineToMiddle keepCaretColumn
        | NormalCommand.ScrollCaretLineToBottom keepCaretColumn -> x.ScrollCaretLineToBottom keepCaretColumn
        | NormalCommand.SubstituteCharacterAtCaret -> x.SubstituteCharacterAtCaret count registerName
        | NormalCommand.SubtractFromWord -> x.SubtractFromWord count
        | NormalCommand.ShiftLinesLeft -> x.ShiftLinesLeft count
        | NormalCommand.ShiftLinesRight -> x.ShiftLinesRight count
        | NormalCommand.ShiftMotionLinesLeft motion -> x.RunWithMotion motion x.ShiftMotionLinesLeft
        | NormalCommand.ShiftMotionLinesRight motion -> x.RunWithMotion motion x.ShiftMotionLinesRight
        | NormalCommand.SplitViewHorizontally -> x.SplitViewHorizontally()
        | NormalCommand.SplitViewVertically -> x.SplitViewVertically()
        | NormalCommand.SwitchMode (modeKind, modeArgument) -> x.SwitchMode modeKind modeArgument
        | NormalCommand.SwitchModeVisualCommand visualKind -> x.SwitchModeVisualCommand visualKind data.Count
        | NormalCommand.SwitchPreviousVisualMode -> x.SwitchPreviousVisualMode()
        | NormalCommand.SwitchToSelection caretMovement -> x.SwitchToSelection caretMovement
        | NormalCommand.ToggleFoldUnderCaret -> x.ToggleFoldUnderCaret count
        | NormalCommand.ToggleAllFolds -> x.ToggleAllFolds()
        | NormalCommand.Undo -> x.Undo count
        | NormalCommand.UndoLine -> x.UndoLine()
        | NormalCommand.WriteBufferAndQuit -> x.WriteBufferAndQuit()
        | NormalCommand.Yank motion -> x.RunWithMotion motion (x.YankMotion registerName)
        | NormalCommand.YankLines -> x.YankLines count registerName

    /// Run a VisualCommand against the buffer
    member x.RunVisualCommand command (data: CommandData) (visualSpan: VisualSpan) =

        let streamSelectionSpan = _textView.Selection.StreamSelectionSpan

        // Clear the selection before actually running any Visual Commands.  Selection is one
        // of the items which is preserved along with caret position when we use an edit transaction
        // with the change primitives (EditWithUndoTransaction).  We don't want the selection to
        // reappear during an undo hence clear it now so it's gone.
        _textView.Selection.Clear()

        let registerName = data.RegisterName
        let count = data.CountOrDefault
        match command with
        | VisualCommand.ChangeCase kind -> x.ChangeCaseVisual kind visualSpan
        | VisualCommand.ChangeSelection -> x.ChangeSelection registerName visualSpan
        | VisualCommand.CloseAllFoldsInSelection -> x.CloseAllFoldsInSelection visualSpan
        | VisualCommand.CloseFoldInSelection -> x.CloseFoldInSelection visualSpan
        | VisualCommand.ChangeLineSelection specialCaseBlock -> x.ChangeLineSelection registerName visualSpan specialCaseBlock
        | VisualCommand.DeleteAllFoldsInSelection -> x.DeleteAllFoldInSelection visualSpan
        | VisualCommand.DeleteSelection -> x.DeleteSelection registerName visualSpan
        | VisualCommand.DeleteLineSelection -> x.DeleteLineSelection registerName visualSpan
        | VisualCommand.FilterLines -> x.FilterLinesVisual visualSpan
        | VisualCommand.FormatLines -> x.FormatLinesVisual visualSpan
        | VisualCommand.FoldSelection -> x.FoldSelection visualSpan
        | VisualCommand.GoToFileInSelectionInNewWindow -> x.GoToFileInSelectionInNewWindow visualSpan
        | VisualCommand.GoToFileInSelection -> x.GoToFileInSelection visualSpan
        | VisualCommand.JoinSelection kind -> x.JoinSelection kind visualSpan
        | VisualCommand.InvertSelection columnOnlyInBlock -> x.InvertSelection visualSpan streamSelectionSpan columnOnlyInBlock
        | VisualCommand.MoveCaretToTextObject (motion, textObjectKind)-> x.MoveCaretToTextObject count motion textObjectKind visualSpan
        | VisualCommand.OpenFoldInSelection -> x.OpenFoldInSelection visualSpan
        | VisualCommand.OpenAllFoldsInSelection -> x.OpenAllFoldsInSelection visualSpan
        | VisualCommand.PutOverSelection moveCaretAfterText -> x.PutOverSelection registerName count moveCaretAfterText visualSpan
        | VisualCommand.ReplaceSelection keyInput -> x.ReplaceSelection keyInput visualSpan
        | VisualCommand.ShiftLinesLeft -> x.ShiftLinesLeftVisual count visualSpan
        | VisualCommand.ShiftLinesRight -> x.ShiftLinesRightVisual count visualSpan
        | VisualCommand.SwitchModeInsert atEndOfLine -> x.SwitchModeInsert visualSpan atEndOfLine
        | VisualCommand.SwitchModePrevious -> x.SwitchPreviousMode()
        | VisualCommand.SwitchModeVisual visualKind -> x.SwitchModeVisual visualKind
        | VisualCommand.SwitchModeOtherVisual -> x.SwitchModeOtherVisual visualSpan
        | VisualCommand.ToggleFoldInSelection -> x.ToggleFoldUnderCaret count
        | VisualCommand.ToggleAllFoldsInSelection-> x.ToggleAllFolds()
        | VisualCommand.YankLineSelection -> x.YankLineSelection registerName visualSpan
        | VisualCommand.YankSelection -> x.YankSelection registerName visualSpan
        | VisualCommand.CutSelection -> x.CutSelection streamSelectionSpan
        | VisualCommand.CopySelection -> x.CopySelection streamSelectionSpan
        | VisualCommand.CutSelectionAndPaste -> x.CutSelectionAndPaste streamSelectionSpan
        | VisualCommand.SelectAll -> x.SelectAll()

    /// Get the MotionResult value for the provided MotionData and pass it
    /// if found to the provided function
    member x.RunWithMotion (motion: MotionData) func =
        match _motionUtil.GetMotion motion.Motion motion.MotionArgument with
        | None ->
            _commonOperations.Beep()
            CommandResult.Error
        | Some data ->
            func data

    /// Process the m[a-z] command
    member x.SetMarkToCaret c =
        match Mark.OfChar c with
        | None ->
            _statusUtil.OnError Resources.Common_MarkInvalid
            _commonOperations.Beep()
            CommandResult.Error
        | Some mark ->
            let line, column = SnapshotPointUtil.GetLineColumn x.CaretPoint
            if not (_markMap.SetMark mark _vimBufferData line column) then
                // Mark set can fail if the user chooses a readonly mark like '<'
                _commonOperations.Beep()
            CommandResult.Completed ModeSwitch.NoSwitch

    /// Get the current number of spaces to caret we are maintaining
    member x.GetSpacesToCaret () =
        let spacesToCaret = _commonOperations.GetSpacesToPoint x.CaretPoint
        match _commonOperations.MaintainCaretColumn with
        | MaintainCaretColumn.None -> spacesToCaret
        | MaintainCaretColumn.Spaces spaces -> max spaces spacesToCaret
        | MaintainCaretColumn.EndOfLine -> spacesToCaret

    /// Restore spaces to caret, or move to start of line if 'startofline' is set
    member x.RestoreSpacesToCaret (spacesToCaret: int) (useStartOfLine: bool) =

        // First apply scroll offset.
        _commonOperations.AdjustCaretForScrollOffset()

        // At this point the view has been scolled and the caret is on the proper line.  Need to
        // adjust the caret within the line to the appropriate column
        if useStartOfLine && _globalSettings.StartOfLine then
            let point = SnapshotLineUtil.GetFirstNonBlankOrEnd x.CaretLine
            TextViewUtil.MoveCaretToPoint _textView point
        else
            if _vimTextBuffer.UseVirtualSpace then
                VirtualSnapshotLineUtil.GetSpace x.CaretLine spacesToCaret _localSettings.TabStop
            else
                SnapshotLineUtil.GetSpaceOrEnd x.CaretLine spacesToCaret _localSettings.TabStop
                |> VirtualSnapshotPointUtil.OfPoint
            |> TextViewUtil.MoveCaretToVirtualPoint _textView
            _commonOperations.MaintainCaretColumn <- MaintainCaretColumn.Spaces spacesToCaret

    /// Get the number lines in the current window
    member x.GetWindowLineCount (textViewLines: ITextViewLineCollection) =
        let lineHeight = _textView.LineHeight
        let viewportHeight = _textView.ViewportHeight
        int (floor (viewportHeight / lineHeight))

    /// Scroll the window up / down a specified number of lines.  If a count is provided
    /// that will always be used.  Else we may choose one or the value of the 'scroll'
    /// option
    member x.ScrollLines scrollDirection useScrollOption countOption =

        // Scrolling lines needs to scroll against the visual buffer vs the edit buffer so
        // that we treated folded lines as a single line.  Normally this would mean we need
        // to jump into the Visual Snapshot.  Here we don't though because we end using
        // IViewScroller to scroll and it does it's count based on Visual Lines vs. real lines
        // in the edit buffer

        // Get the number of lines that we should scroll by.
        let count =
            match countOption with
            | Some count ->
                // When a count is provided then we always use that count.  If this is a
                // scroll option version though we do need to update the scroll option to
                // this value
                if useScrollOption then
                    _windowSettings.Scroll <- count
                count
            | None ->
                if useScrollOption then
                    _windowSettings.Scroll
                else
                    1

        // Ensure that we scroll by at least one line.
        let minCount = 1
        let count = max count minCount

        let spacesToCaret = x.GetSpacesToCaret()

        // Update the caret to the specified offset from the first visible line
        let updateCaretToOffset lineOffset =
            match TextViewUtil.GetTextViewLines _textView with
            | None -> ()
            | Some textViewLines ->
                let firstIndex = textViewLines.GetIndexOfTextLine(textViewLines.FirstVisibleLine)
                let textViewLine = textViewLines.[firstIndex + lineOffset]
                let snapshotLine = SnapshotPointUtil.GetContainingLine textViewLine.Start
                _commonOperations.MoveCaretToPoint snapshotLine.Start ViewFlags.Standard

        match TextViewUtil.GetTextViewLines _textView with
        | None -> ()
        | Some textViewLines ->

            // Limit the amount of scrolling to the size of the window.
            let maxCount = x.GetWindowLineCount textViewLines
            let count = min count maxCount

            let firstIndex = textViewLines.GetIndexOfTextLine(textViewLines.FirstVisibleLine)
            let caretIndex = textViewLines.GetIndexOfTextLine(_textView.Caret.ContainingTextViewLine)

            // How many visual lines is the caret offset from the first visible line
            let lineOffset = max 0 (caretIndex - firstIndex)

            match scrollDirection with
            | ScrollDirection.Up ->
                if 0 = textViewLines.FirstVisibleLine.Start.Position then
                    // The buffer is currently scrolled to the very top.  Move the caret by the specified
                    // count or beep if caret at the start of the file as well.  Make sure this movement
                    // occurs on the visual lines, not the edit buffer (other wise folds will cause the
                    // caret to move incorrectly)
                    if caretIndex = 0 then
                        _commonOperations.Beep()
                    else
                        let index = max 0 (caretIndex - count)
                        let line = textViewLines.[index]
                        TextViewUtil.MoveCaretToPoint _textView line.Start
                else
                    _textView.ViewScroller.ScrollViewportVerticallyByLines(scrollDirection, count)
                    updateCaretToOffset lineOffset
            | ScrollDirection.Down ->
                let lastLine = SnapshotUtil.GetLastNormalizedLine _textView.TextSnapshot
                let visualEndPoint = lastLine.End
                if visualEndPoint.Position <= textViewLines.LastVisibleLine.End.Position then
                    // Currently scrolled to the end of the buffer.  Move the caret by the count or
                    // beep if truly at the end
                    let lastIndex = textViewLines.GetIndexOfTextLine(textViewLines.LastVisibleLine)
                    if lastIndex = caretIndex then
                        _commonOperations.Beep()
                    else
                        let index = min (textViewLines.Count - 1) (caretIndex + count)
                        let line = textViewLines.[index]
                        let caretPoint, _ = SnapshotPointUtil.OrderAscending visualEndPoint line.End
                        TextViewUtil.MoveCaretToPoint _textView caretPoint
                else
                    _textView.ViewScroller.ScrollViewportVerticallyByLines(scrollDirection, count)
                    updateCaretToOffset lineOffset
            | _ -> ()

            x.RestoreSpacesToCaret spacesToCaret true

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll pages in the specified direction
    member x.ScrollPages direction count =

        // Get whether this scroll is up or down.
        let getIsUp direction =
            match direction with
            | ScrollDirection.Up -> Some true
            | ScrollDirection.Down -> Some false
            | _ -> None

        // Get the page scroll amount in lines, allowing for
        /// some overlapping context lines (vim overlaps two).
        let getScrollAmount (textViewLines: ITextViewLineCollection) =
            let lineCount = x.GetWindowLineCount textViewLines
            max 1 (lineCount - 2)

        // Scroll up by one full page unless we are at the top.
        let doScrollUp () =
            match TextViewUtil.GetTextViewLines _textView with
            | None ->
                _editorOperations.PageUp(false)
            | Some textViewLines ->
                let scrollAmount = getScrollAmount textViewLines
                _textView.ViewScroller.ScrollViewportVerticallyByLines(ScrollDirection.Up, scrollAmount)

        // Scroll down by one full page or as much as possible.
        let doScrollDown () =
            match TextViewUtil.GetTextViewLines _textView with
            | None ->
                _editorOperations.PageDown(false)
            | Some textViewLines ->

                // Check whether the last line is visible.
                let lastVisiblePoint = textViewLines.LastVisibleLine.EndIncludingLineBreak
                let lastVisibleLine = SnapshotPointUtil.GetContainingLine lastVisiblePoint
                let lastVisibleLineNumber = lastVisibleLine.LineNumber
                let lastLine = SnapshotUtil.GetLastNormalizedLine _textView.TextSnapshot
                let lastLineNumber = SnapshotLineUtil.GetLineNumber lastLine
                if lastVisibleLineNumber >= lastLineNumber then

                    // The last line is already visible. Move the caret
                    // to the last line and scroll it to the top of the view.
                    _textView.Caret.MoveTo(lastLine.Start) |> ignore
                    _editorOperations.ScrollLineTop()
                else
                    let scrollAmount = getScrollAmount textViewLines
                    _textView.ViewScroller.ScrollViewportVerticallyByLines(ScrollDirection.Down, scrollAmount)

        // Get the last (and if possible, fully visible) line in the text view.
        let getLastFullyVisibleLine (textViewLines: ITextViewLineCollection) =
            let lastLine = textViewLines.LastVisibleLine
            if lastLine.VisibilityState = Formatting.VisibilityState.FullyVisible then
                lastLine
            else

                // The last line is only partially visible. This could be either because
                // the view is scrolled so that the bottom of the text row is clipped,
                // or because line wrapping is in effect and there are off-screen
                // wrapped text view lines. In either case, try to move to the text
                // view line corresponding to the previous snapshot line.
                let partialLine = SnapshotPointUtil.GetContainingLine lastLine.Start
                let previousLineNumber = partialLine.LineNumber - 1
                let previousLine = SnapshotUtil.GetLineOrFirst _textView.TextSnapshot previousLineNumber
                let textViewLine = textViewLines.GetTextViewLineContainingBufferPosition previousLine.Start
                if textViewLine.VisibilityState = Formatting.VisibilityState.FullyVisible then
                    textViewLine
                else
                    lastLine

        let spacesToCaret = x.GetSpacesToCaret()

        match getIsUp direction with
        | None ->
            _commonOperations.Beep()

        | Some isUp ->

            // Do the scrolling.
            for i = 1 to count do
                if isUp then doScrollUp() else doScrollDown()

            // Adjust the caret by, roughly, putting the cursor on the
            // first non-blank character of the last visible line
            // when scrolling up, and on the first non-blank character
            // of the first visible line when scrolling down.
            match TextViewUtil.GetTextViewLines _textView with
            | None ->
                ()

            | Some textViewLines ->

                // Find a text view line belonging to the snapshot line that
                // should contain the caret.
                let textViewLine =
                    if isUp then

                        // As a special case when scrolling up, if the caret
                        // line is already visible on the screen, use that line.
                        let caretLine = _textView.Caret.ContainingTextViewLine
                        if caretLine.VisibilityState = VisibilityState.FullyVisible then
                            caretLine
                        else
                            getLastFullyVisibleLine textViewLines

                    else
                        textViewLines.FirstVisibleLine

                // Find the snapshot line corresponding to the text view line.
                let line = SnapshotPointUtil.GetContainingLine textViewLine.Start

                // Move the caret to the beginning of that line.
                _textView.Caret.MoveTo(line.Start) |> ignore

                x.RestoreSpacesToCaret spacesToCaret true

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll the window in the specified direction by the specified number of lines.  The
    /// caret only moves if it leaves the view port
    member x.ScrollWindow direction count =

        // Count the number of rows we need to scroll to move count
        // lines off the screen in the specified direction.
        let rowCount =
            if not _windowSettings.Wrap then
                count
            else

                // If line wrapping is in effect, there can be multiple screen rows
                // corrsponding to a single text buffer line.
                match TextViewUtil.GetTextViewLines _textView with
                | None -> count
                | Some textViewLines ->

                    // Build an array of the text view line indexes that correspond
                    // to the first segment of a fully visible text buffer line.
                    // These are the rows that have a line number next to them
                    // when line numbering is turned on.
                    let numberedLineIndexes =
                        textViewLines
                        |> Seq.where (fun textViewLine ->
                            textViewLine.VisibilityState = Formatting.VisibilityState.FullyVisible &&
                                textViewLine.Start = textViewLine.Start.GetContainingLine().Start)
                        |> Seq.map (fun textViewLine ->
                            textViewLines.GetIndexOfTextLine(textViewLine))
                        |> Seq.toArray
                    let lastNumberedLineIndex = numberedLineIndexes.Length - 1

                    // Use the numbered line indexes to count screen rows used by
                    // lines visible in the text view.
                    if count <= lastNumberedLineIndex then
                        match direction with
                        | ScrollDirection.Up ->

                            // Calculate how many rows the last fully visible line uses.
                            let rec getWrapCount (index: int) =
                                if index <= textViewLines.Count - 2 then

                                    // Does the current text view line belong to the same
                                    // text buffer line as the next text view line?
                                    let currentLine = textViewLines.[index].Start.GetContainingLine()
                                    let nextLine = textViewLines.[index + 1].Start.GetContainingLine()
                                    if currentLine.LineNumber = nextLine.LineNumber then
                                        let wrapCount = getWrapCount (index + 1)
                                        wrapCount + 1
                                    else
                                        1
                                else
                                    1

                            let lastIndex = numberedLineIndexes.[lastNumberedLineIndex]
                            let targetIndex = numberedLineIndexes.[lastNumberedLineIndex - (count - 1)]
                            let lastWrapCount = getWrapCount lastIndex
                            lastIndex - targetIndex + lastWrapCount
                        | ScrollDirection.Down ->
                            let firstIndex = numberedLineIndexes.[0]
                            let targetIndex = numberedLineIndexes.[count]
                            targetIndex - firstIndex
                        | _ -> count
                    else
                        count

        // In case something went wrong when calculating the row count,
        // it should always be at least at big as count.
        let rowCount = max rowCount count

        let spacesToCaret = x.GetSpacesToCaret()

        _textView.ViewScroller.ScrollViewportVerticallyByLines(direction, rowCount)

        match TextViewUtil.GetVisibleSnapshotLineRange _textView with
        | None -> ()
        | Some lineRange ->
            match direction with
            | ScrollDirection.Up ->
                if x.CaretPoint.Position > lineRange.End.Position then
                    TextViewUtil.MoveCaretToPoint _textView lineRange.End
            | ScrollDirection.Down ->
                if x.CaretPoint.Position < lineRange.Start.Position then
                    TextViewUtil.MoveCaretToPoint _textView lineRange.Start
            | _ -> ()

            x.RestoreSpacesToCaret spacesToCaret false

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll the line containing the caret to the top of the ITextView.
    member x.ScrollCaretLineToTop keepCaretColumn =
        _commonOperations.EditorOperations.ScrollLineTop()
        if not keepCaretColumn then
            _commonOperations.EditorOperations.MoveToStartOfLineAfterWhiteSpace(false)
        _commonOperations.EnsureAtCaret ViewFlags.ScrollOffset
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll the line containing the caret to the middle of the ITextView.
    member x.ScrollCaretLineToMiddle keepCaretColumn =
        _commonOperations.EditorOperations.ScrollLineCenter()
        if not keepCaretColumn then
            _commonOperations.EditorOperations.MoveToStartOfLineAfterWhiteSpace(false)
        _commonOperations.EnsureAtCaret ViewFlags.ScrollOffset
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll the line containing the caret to the bottom of the ITextView.
    member x.ScrollCaretLineToBottom keepCaretColumn =
        _commonOperations.EditorOperations.ScrollLineBottom()
        if not keepCaretColumn then
            _commonOperations.EditorOperations.MoveToStartOfLineAfterWhiteSpace(false)
        _commonOperations.EnsureAtCaret ViewFlags.ScrollOffset
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Shift the given line range left by the specified value.  The caret will be
    /// placed at the first character on the first line of the shifted text
    member x.ShiftLinesLeftCore range multiplier =

        // Use a transaction so the caret will be properly moved for undo / redo
        x.EditWithUndoTransaction "ShiftLeft" (fun () ->
            _commonOperations.ShiftLineRangeLeft range multiplier

            // Now move the caret to the first non-whitespace character on the first
            // line
            let line = SnapshotUtil.GetLine x.CurrentSnapshot range.StartLineNumber
            let point =
                match SnapshotLineUtil.GetFirstNonBlank line with
                | None -> SnapshotLineUtil.GetLastIncludedPoint line |> OptionUtil.getOrDefault line.Start
                | Some point -> point
            TextViewUtil.MoveCaretToPoint _textView point)

    /// Shift the given line range left by the specified value.  The caret will be
    /// placed at the first character on the first line of the shifted text
    member x.ShiftLinesRightCore range multiplier =

        // Use a transaction so the caret will be properly moved for undo / redo
        x.EditWithUndoTransaction "ShiftRight" (fun () ->
            _commonOperations.ShiftLineRangeRight range multiplier

            // Now move the caret to the first non-whitespace character on the first
            // line
            let line = SnapshotUtil.GetLine x.CurrentSnapshot range.StartLineNumber
            let point =
                match SnapshotLineUtil.GetFirstNonBlank line with
                | None -> SnapshotLineUtil.GetLastIncludedPoint line |> OptionUtil.getOrDefault line.Start
                | Some point -> point
            TextViewUtil.MoveCaretToPoint _textView point)

    /// Shift 'count' lines to the left
    member x.ShiftLinesLeft count =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        x.ShiftLinesLeftCore range 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Shift 'motion' lines to the left by 'count' shiftwidth.
    member x.ShiftLinesLeftVisual count visualSpan =

        // Both Character and Line spans operate like most shifts
        match visualSpan with
        | VisualSpan.Character characterSpan ->
            let range = SnapshotLineRangeUtil.CreateForSpan characterSpan.Span
            x.ShiftLinesLeftCore range count
        | VisualSpan.Line range ->
            x.ShiftLinesLeftCore range count
        | VisualSpan.Block blockSpan ->
            // Shifting a block span is trickier because it doesn't shift at column
            // 0 but rather shifts at the start column of every span.  It also treats
            // the caret much more different by keeping it at the start of the first
            // span vs. the start of the shift
            let targetCaretPosition = visualSpan.Start.Position

            // Use a transaction to preserve the caret.  But move the caret first since
            // it needs to be undone to this location
            TextViewUtil.MoveCaretToPosition _textView targetCaretPosition
            x.EditWithUndoTransaction "ShiftLeft" (fun () ->
                _commonOperations.ShiftLineBlockLeft blockSpan.BlockSpans count
                TextViewUtil.MoveCaretToPosition _textView targetCaretPosition)

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Shift 'count' lines to the right
    member x.ShiftLinesRight count =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        x.ShiftLinesRightCore range 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Shift 'motion' lines to the right by 'count' shiftwidth
    member x.ShiftLinesRightVisual count visualSpan =

        // Both Character and Line spans operate like most shifts
        match visualSpan with
        | VisualSpan.Character characterSpan ->
            let range = SnapshotLineRangeUtil.CreateForSpan characterSpan.Span
            x.ShiftLinesRightCore range count
        | VisualSpan.Line range ->
            x.ShiftLinesRightCore range count
        | VisualSpan.Block blockSpan ->
            // Shifting a block span is trickier because it doesn't shift at column
            // 0 but rather shifts at the start column of every span.  It also treats
            // the caret much more different by keeping it at the start of the first
            // span vs. the start of the shift
            let targetCaretPosition = visualSpan.Start.Position

            // Use a transaction to preserve the caret.  But move the caret first since
            // it needs to be undone to this location
            TextViewUtil.MoveCaretToPosition _textView targetCaretPosition
            x.EditWithUndoTransaction "ShiftLeft" (fun () ->
                _commonOperations.ShiftLineBlockRight blockSpan.BlockSpans count

                TextViewUtil.MoveCaretToPosition _textView targetCaretPosition)

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Shift 'motion' lines to the left
    member x.ShiftMotionLinesLeft (result: MotionResult) =
        x.ShiftLinesLeftCore result.LineRange 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Shift 'motion' lines to the right
    member x.ShiftMotionLinesRight (result: MotionResult) =
        x.ShiftLinesRightCore result.LineRange 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Split the view horizontally
    member x.SplitViewHorizontally () =
        _vimHost.SplitViewHorizontally _textView
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Split the view vertically
    member x.SplitViewVertically () =
        _vimHost.SplitViewVertically _textView
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Substitute 'count' characters at the cursor on the current line.  Very similar to
    /// DeleteCharacterAtCaret.  Main exception is the behavior when the caret is on
    /// or after the last character in the line
    /// should be after the span for Substitute even if 've='.
    member x.SubstituteCharacterAtCaret count registerName =

        x.EditWithLinkedChange "Substitute" (fun () ->
            if x.CaretPoint.Position >= x.CaretLine.End.Position then
                // When we are past the end of the line just move the caret
                // to the end of the line and complete the command.  Nothing should be deleted
                TextViewUtil.MoveCaretToPoint _textView x.CaretLine.End
            else
                let endPoint = SnapshotLineUtil.GetColumnOrEnd (x.CaretColumn + count) x.CaretLine
                let span = SnapshotSpan(x.CaretPoint, endPoint)

                // Use a transaction so we can guarantee the caret is in the correct
                // position on undo / redo
                x.EditWithUndoTransaction "DeleteChar" (fun () ->
                    let position = x.CaretPoint.Position
                    let snapshot = TextBufferUtil.DeleteAndGetLatest _textBuffer span.Span
                    TextViewUtil.MoveCaretToPoint _textView (SnapshotPoint(snapshot, position)))

                // Put the deleted text into the specified register
                let value = RegisterValue(StringData.OfSpan span, OperationKind.CharacterWise)
                _commonOperations.SetRegisterValue registerName RegisterOperation.Delete value)

    /// Subtract 'count' values from the word under the caret
    member x.SubtractFromWord count =
        x.AddToWord -count

    /// Switch to the given mode
    member x.SwitchMode modeKind modeArgument =
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (modeKind, modeArgument))

    /// Switch to the visual mode specified by 'selectmode=cmd'
    member x.SwitchModeVisualCommand visualKind count =
        match count, _vimData.LastVisualSelection with
        | Some count, Some lastSelection ->
            let visualSelection = lastSelection.GetVisualSelection x.CaretPoint count
            let modeKind =
                match lastSelection with
                | StoredVisualSelection.Character _ -> ModeKind.VisualCharacter
                | StoredVisualSelection.CharacterLine _ -> ModeKind.VisualCharacter
                | StoredVisualSelection.Line _ -> ModeKind.VisualLine
            let visualSelection = VisualSelection.CreateForward visualSelection.VisualSpan
            let arg = ModeArgument.InitialVisualSelection (visualSelection, None)
            CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (modeKind, arg))
        | _ ->
            let modeKind =
                if Util.IsFlagSet _globalSettings.SelectModeOptions SelectModeOptions.Command then
                    visualKind.SelectModeKind
                else
                    visualKind.VisualModeKind
            CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (modeKind, ModeArgument.None))

    /// Switch to the previous Visual Span selection
    member x.SwitchPreviousVisualMode () =
        match _vimTextBuffer.LastVisualSelection with
        | None ->
            // If there is no available previous visual span then raise an error
            _statusUtil.OnError Resources.Common_NoPreviousVisualSpan
            CommandResult.Error

        | Some visualSelection ->
            let modeKind = visualSelection.VisualKind.VisualModeKind
            let modeArgument = ModeArgument.InitialVisualSelection (visualSelection, None)
            x.SwitchMode modeKind modeArgument

    /// Move the caret to the specified motion.  How this command is implemented is largely dependent
    /// upon the values of 'keymodel' and 'selectmode'.  It will either move the caret potentially as
    /// a motion or initiate a select in the editor
    member x.SwitchToSelection caretMovement =
        let anchorPoint = x.CaretPoint
        if not (_commonOperations.MoveCaretWithArrow caretMovement) then
            CommandResult.Error
        else
            let visualSelection = VisualSelection.CreateForPoints VisualKind.Character anchorPoint x.CaretPoint _localSettings.TabStop
            let visualSelection = visualSelection.AdjustForSelectionKind _globalSettings.SelectionKind
            let modeKind =
                if Util.IsFlagSet _globalSettings.SelectModeOptions SelectModeOptions.Keyboard then
                    ModeKind.SelectCharacter
                else
                    ModeKind.VisualCharacter
            let argument = ModeArgument.InitialVisualSelection(visualSelection, None)
            CommandResult.Completed (ModeSwitch.SwitchModeWithArgument(modeKind, argument))

    /// Switch from the current visual mode into insert.  If we are in block mode this
    /// will start a block insertion
    member x.SwitchModeInsert (visualSpan: VisualSpan) (atEndOfLine: bool) =

        match visualSpan with
        | VisualSpan.Block blockSpan ->
            // The insert begins at the start of the block collection.  Any undo should move
            // the caret back to this position so make sure to move it before we start the
            // transaction so that it will be properly positioned on undo
            if atEndOfLine then
                visualSpan.Start
                |> SnapshotPointUtil.GetContainingLine
                |> SnapshotLineUtil.GetEnd
            else
                visualSpan.Start
            |> TextViewUtil.MoveCaretToPoint _textView
            x.EditBlockWithLinkedChange "Visual Insert" blockSpan atEndOfLine (fun _ -> ())
        | _ ->
            // For all other visual mode inserts the caret moves to column 0 on the first
            // line of the selection.  It should be positioned there after an undo so move
            // it now before the undo transaction
            visualSpan.Start
            |> SnapshotPointUtil.GetContainingLine
            |> if atEndOfLine then SnapshotLineUtil.GetEnd else SnapshotLineUtil.GetStart
            |> TextViewUtil.MoveCaretToPoint _textView
            x.EditWithUndoTransaction "Visual Insert" (fun _ -> ())
            x.SwitchMode ModeKind.Insert ModeArgument.None

    /// Switch to the previous mode
    member x.SwitchPreviousMode() =
        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Switch to other visual mode: visual from select or vice versa
    member x.SwitchModeOtherVisual visualSpan =
        let currentModeKind = _vimBufferData.VimTextBuffer.ModeKind
        match VisualKind.OfModeKind currentModeKind with
        | Some visualKind ->
            let newModeKind =
                if VisualKind.IsAnySelect currentModeKind then
                    visualKind.VisualModeKind
                else
                    visualKind.SelectModeKind
            x.SwitchMode newModeKind ModeArgument.None
        | None ->
            _commonOperations.Beep()
            CommandResult.Completed ModeSwitch.NoSwitch

    /// Switch from the current visual mode into the specified visual mode
    member x.SwitchModeVisual newVisualKind =

        let badOperation () =
            _commonOperations.Beep()
            CommandResult.Completed ModeSwitch.NoSwitch

        // The anchor point is the original anchor point of the visual session
        let anchorPoint =
            _vimBufferData.VisualAnchorPoint
            |> OptionUtil.map2 (TrackingPointUtil.GetPoint x.CurrentSnapshot)
        match anchorPoint with
        | None -> badOperation ()
        | Some anchorPoint ->

            match _vimTextBuffer.ModeKind |> VisualKind.OfModeKind with
            | None -> badOperation ()
            | Some currentVisualKind ->
                if currentVisualKind = newVisualKind then
                    // Switching to the same mode just goes back to normal
                    x.SwitchMode ModeKind.Normal ModeArgument.None
                else
                    let caretPoint = x.CaretPoint
                    let newVisualSelection = VisualSelection.CreateForPoints newVisualKind anchorPoint caretPoint _localSettings.TabStop
                    let modeArgument = ModeArgument.InitialVisualSelection (newVisualSelection, Some anchorPoint)

                    x.SwitchMode newVisualSelection.VisualKind.VisualModeKind modeArgument

    /// Undo count operations in the ITextBuffer
    member x.Undo count =
        use guard = new NormalModeSelectionGuard(_vimBufferData)
        _commonOperations.Undo count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Undo all recent changes made to the current line
    member x.UndoLine () =
        if not (_lineChangeTracker.Swap()) then
            _commonOperations.Beep()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Write out the ITextBuffer and quit
    member x.WriteBufferAndQuit () =
        let result =
            if _vimHost.IsDirty _textBuffer then
                _vimHost.Save _textBuffer
            else
                true

        if result then
            _vimHost.Close _textView
            CommandResult.Completed ModeSwitch.NoSwitch
        else
            CommandResult.Error

    /// Yank the specified lines into the specified register.  This command should operate
    /// against the visual buffer if possible.  Yanking a line which contains the fold should
    /// yank the entire fold
    member x.YankLines count registerName =
        let span = x.EditWithVisualSnapshot (fun x ->

            // Get the line range in the snapshot data
            let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
            range.ExtentIncludingLineBreak)

        match span with
        | None ->
            // If we couldn't map back down raise an error
            _statusUtil.OnError Resources.Internal_ErrorMappingToVisual
        | Some span ->

            let data = StringData.OfSpan span
            let value = x.CreateRegisterValue data OperationKind.LineWise
            _commonOperations.SetRegisterValue registerName RegisterOperation.Yank value
            _commonOperations.RecordLastYank span

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Yank the contents of the motion into the specified register
    member x.YankMotion registerName (result: MotionResult) =
        let value = x.CreateRegisterValue (StringData.OfSpan result.Span) result.OperationKind
        _commonOperations.SetRegisterValue registerName RegisterOperation.Yank value
        match result.OperationKind with
        | OperationKind.CharacterWise ->
            TextViewUtil.MoveCaretToPoint _textView result.Start
        | _ -> ()
        _commonOperations.RecordLastYank result.Span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Yank the lines in the specified selection
    member x.YankLineSelection registerName (visualSpan: VisualSpan) =
        let editSpan, operationKind =
            match visualSpan with
            | VisualSpan.Character characterSpan ->
                // Extend the character selection to the full lines
                let range = SnapshotLineRangeUtil.CreateForSpan characterSpan.Span
                EditSpan.Single range.ExtentIncludingLineBreak, OperationKind.LineWise
            | VisualSpan.Line _ ->
                // Simple case, just use the visual span as is
                visualSpan.EditSpan, OperationKind.LineWise
            | VisualSpan.Block _ ->
                // Odd case.  Don't treat any different than a normal yank
                visualSpan.EditSpan, visualSpan.OperationKind

        let data = StringData.OfEditSpan editSpan
        let value = x.CreateRegisterValue data operationKind
        _commonOperations.SetRegisterValue registerName RegisterOperation.Yank value
        _commonOperations.RecordLastYank editSpan.OverarchingSpan
        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Yank the selection into the specified register
    member x.YankSelection registerName (visualSpan: VisualSpan) =
        let data = StringData.OfEditSpan visualSpan.EditSpan
        let value = x.CreateRegisterValue data visualSpan.OperationKind
        _commonOperations.SetRegisterValue registerName RegisterOperation.Yank value
        _commonOperations.RecordLastYank visualSpan.EditSpan.OverarchingSpan
        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Cut selection
    member x.CutSelection streamSelectionSpan =
        _textView.Selection.Select(streamSelectionSpan.Start, streamSelectionSpan.End)
        _editorOperations.CutSelection() |> ignore
        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Copy selection
    member x.CopySelection streamSelectionSpan =
        _textView.Selection.Select(streamSelectionSpan.Start, streamSelectionSpan.End)
        _editorOperations.CopySelection() |> ignore
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Cut selection and paste
    member x.CutSelectionAndPaste streamSelectionSpan =
        _textView.Selection.Select(streamSelectionSpan.Start, streamSelectionSpan.End)
        _editorOperations.Paste() |> ignore
        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Select the whole document
    member x.SelectAll () =
        let extent = SnapshotUtil.GetExtent _textBuffer.CurrentSnapshot
        _textView.Selection.Select(extent, false)
        CommandResult.Completed ModeSwitch.NoSwitch

    interface ICommandUtil with
        member x.RunNormalCommand command data = x.RunNormalCommand command data
        member x.RunVisualCommand command data visualSpan = x.RunVisualCommand command data visualSpan
        member x.RunInsertCommand command = x.RunInsertCommand command
        member x.RunCommand command = x.RunCommand command

