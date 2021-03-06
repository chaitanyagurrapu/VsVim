﻿using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Vim.UnitTest
{
    public abstract class SnapshotCodePointTest : VimTestBase
    {
        private static void AssertIt(SnapshotCodePoint point, int? codePoint = null, CodePointInfo codePointInfo = null, string text = null, int? position = null)
        {
            if (codePoint.HasValue)
            {
                Assert.Equal(codePoint.Value, point.CodePoint);
            }

            if (codePointInfo != null)
            {
                Assert.Equal(codePointInfo, point.CodePointInfo);
            }

            if (text != null)
            {
                Assert.Equal(text, point.GetText());
            }

            if (position != null)
            {
                Assert.Equal(position.Value, point.Point.Position);
            }
        }

        public sealed class Constructors : SnapshotCodePointTest
        {
            private SnapshotCodePoint Create(string text, int position)
            {
                var textBuffer = CreateTextBuffer(text);
                var point = new SnapshotPoint(textBuffer.CurrentSnapshot, position);
                return new SnapshotCodePoint(point);
            }

            [WpfFact]
            public void HighCharacter()
            {
                var point =  Create("A𠈓C", 1);
                AssertIt(point, codePointInfo: CodePointInfo.SurrogatePairHighCharacter, text: "𠈓");
            }

            [WpfFact]
            public void LowCharacter()
            {
                var point =  Create("A𠈓C", 2);
                AssertIt(point, codePointInfo: CodePointInfo.SurrogatePairHighCharacter, text: "𠈓", position: 1);
            }

            [WpfFact]
            public void SimpleCharacter()
            {
                var point =  Create("A𠈓C", 0);
                AssertIt(point, codePointInfo: CodePointInfo.SimpleCharacter, text: "A", position: 0);
            }
        }

        public sealed class AddSubractTests : SnapshotCodePointTest
        {
            [WpfFact]
            public void AddSimple()
            {
                var textBuffer = CreateTextBuffer("A𠈓C");
                var point = new SnapshotCodePoint(textBuffer.GetStartPoint());
                AssertIt(point.Add(1), codePointInfo: CodePointInfo.SurrogatePairHighCharacter, text: "𠈓", position: 1);
                AssertIt(point.Add(2), codePointInfo: CodePointInfo.SimpleCharacter, text: "C", position: 3);
                AssertIt(point.Add(3), codePointInfo: CodePointInfo.EndPoint);
            }

            [WpfFact]
            public void AddPastEnd()
            {
                var textBuffer = CreateTextBuffer("A𠈓C");
                var point = new SnapshotCodePoint(textBuffer.GetStartPoint());
                Assert.Throws<ArgumentOutOfRangeException>(() => point.Add(4));
            }

            [WpfFact]
            public void SubtractSimple()
            {
                var textBuffer = CreateTextBuffer("A𠈓C");
                var point = new SnapshotCodePoint(textBuffer.GetEndPoint());
                AssertIt(point.Subtract(1), codePointInfo: CodePointInfo.SimpleCharacter, text: "C", position: 3);
                AssertIt(point.Subtract(2), codePointInfo: CodePointInfo.SurrogatePairHighCharacter, text: "𠈓", position: 1);
                AssertIt(point.Subtract(3), codePointInfo: CodePointInfo.SimpleCharacter, text: "A", position: 0);
            }

            [WpfFact]
            public void SubtractPastEnd()
            {
                var textBuffer = CreateTextBuffer("A𠈓C");
                var point = new SnapshotCodePoint(textBuffer.GetEndPoint());
                Assert.Throws<ArgumentOutOfRangeException>(() => point.Add(4));
            }
        }
    }
}
