﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Winterdom.Viasfora.Util;
using Winterdom.Viasfora.Contracts;

namespace Winterdom.Viasfora.Rainbow {
  public class TextBufferBraces : ITextBufferBraces {
    private List<BracePos> braces;
    private List<CharPos> braceErrors;
    private SortedList<char, char> braceList;
    private IBraceScanner braceScanner;
    private ILanguage language;
    public ITextSnapshot Snapshot { get; private set; }
    public String BraceChars { get; private set; }
    public int LastParsedPosition { get; private set; }
    public bool Enabled {
      get { return language != null ? language.Enabled : false; }
    }

    public TextBufferBraces(ITextSnapshot snapshot, ILanguage language) {
      this.Snapshot = snapshot;
      this.LastParsedPosition = -1;
      this.language = language;
      this.braceList = new SortedList<char, char>();
      this.braces = new List<BracePos>();
      this.braceErrors = new List<CharPos>();

      if ( this.language != null ) {
        this.braceScanner = this.language.GetService<IBraceScanner>();

        this.braceList.Clear();
        this.BraceChars = this.braceScanner.BraceList;
        for ( int i = 0; i < BraceChars.Length; i += 2 ) {
          this.braceList.Add(BraceChars[i], BraceChars[i + 1]);
        }
      }
    }

    public void Invalidate(SnapshotPoint startPoint) {
      if ( ScanIsUnnecessary() ) return;
      // the new start belongs to a different snapshot!
      var newSnapshot = startPoint.Snapshot;
      this.Snapshot = newSnapshot;

      // remove everything cached after the startPoint
      int index = FindIndexOfBraceBefore(startPoint.Position);
      index = AdjustForInvalidation(index);
      if ( index >= 0 ) {
        // index is before startPoint
        InvalidateFromBraceAtIndex(newSnapshot, index+1);
      } else {
        // there are no braces found before this position
        // so invalidate all
        InvalidateFromBraceAtIndex(newSnapshot, 0);
      }

      this.InvalidateBraceErrorsFromPos(startPoint.Position);
    }

    private int AdjustForInvalidation(int index) {
      // index is the last known good brace
      // ask the extractor if it can resume from there
      // or skip backwards until we find one
      // that is resumable
      int newIndex = index;
      IResumeControl control = this.braceScanner as IResumeControl;
      if ( control != null ) {
        for ( ; newIndex > 0; newIndex-- ) {
          if ( control.CanResume(braces[newIndex].ToCharPos()) )
            break;
        }
      }
      // Notice that we actually resume from the newIndex + 1
      // so go back one more
      if ( newIndex > 0 && newIndex != index )
        newIndex--;
      return newIndex;
    }

    public void UpdateSnapshot(ITextSnapshot snapshot) {
      this.Snapshot = snapshot;
    }

    public IEnumerable<BracePos> BracesInSpans(NormalizedSnapshotSpanCollection spans) {
      if ( ScanIsUnnecessary() ) yield break;

      for ( int i = 0; i < spans.Count; i++ ) {
        var wantedSpan = spans[i];
        EnsureLinesInPreferredSpan(wantedSpan);
        int startIndex = FindIndexOfBraceAtOrAfter(wantedSpan.Start);
        if ( startIndex < 0 ) {
          continue;
        }
        for ( int j = startIndex; j < braces.Count; j++ ) {
          BracePos bp = braces[j];
          if ( bp.Position > wantedSpan.End ) break;
          yield return bp;
        }
      }
    }

    public IEnumerable<CharPos> ErrorBracesInSpans(NormalizedSnapshotSpanCollection spans) {
      if ( ScanIsUnnecessary() )
        return Enumerable.Empty<CharPos>();

      // we expect there to be very few brace errors,
      // so it's not worth optimizing this too much
      EnsureLinesInPreferredSpan(spans.Complete());
      if ( this.braceErrors.Count == 0 )
        return Enumerable.Empty<CharPos>();

      return from e in this.braceErrors
             from span in spans
             where e.Position >= span.Start
                && e.Position <= span.End
             select e;
    }

    public IEnumerable<BracePos> BracesFromPosition(int position) {
      if ( ScanIsUnnecessary() ) return new BracePos[0];
      SnapshotSpan span = new SnapshotSpan(Snapshot, position, Snapshot.Length - position);
      return BracesInSpans(new NormalizedSnapshotSpanCollection(span));
    }

    // returns the brace pair when the point is already a brace
    public Tuple<BracePos, BracePos> GetBracePair(SnapshotPoint point) {
      if ( point.Snapshot != this.Snapshot || point.Position >= Snapshot.Length ) {
        return null;
      }
      int index = FindIndexOfBraceAtOrAfter(point.Position);
      if ( index < 0 ) return null;
      BracePos one = this.braces[index];
      if ( one.Position != point.Position ) {
        return null;
      }

      if ( IsOpeningBrace(one.Brace) ) {
        return GetBracePairFromPosition(point, RainbowHighlightMode.TrackNextScope);
      } else {
        return GetBracePairFromPosition(point, RainbowHighlightMode.TrackInsertionPoint);
      }
    }

    public Tuple<BracePos, BracePos> GetBracePairFromPosition(SnapshotPoint point, RainbowHighlightMode mode) {
      if ( point.Snapshot != this.Snapshot || point.Position >= Snapshot.Length ) {
        return null;
      }

      int openIndex = -1;
      BracePos? opening = null;


      if ( mode == RainbowHighlightMode.TrackInsertionPoint ) {
        opening = FindClosestOpeningBrace(point.Position, out openIndex);
      } else {
        opening = CheckForBraceAtPositionOrClosestOpeningBrace(point.Position, out openIndex);
      }
      if ( opening == null ) {
        return null;
      }

      for ( int i = openIndex + 1; i < this.braces.Count; i++ ) {
        if ( i == this.braces.Count - 1 ) {
          // continue parsing the document if necessary
          this.ContinueParsing(this.LastParsedPosition, this.Snapshot.Length);
        }
        BracePos closing = this.braces[i];
        if ( this.IsOpeningBrace(closing.Brace) )
          continue;
        if ( this.braceList[opening.Value.Brace] == closing.Brace
            && closing.Depth == opening.Value.Depth ) {
          return new Tuple<BracePos, BracePos>(opening.Value, closing);
        }
      }
      return null;
    }

    private BracePos? CheckForBraceAtPositionOrClosestOpeningBrace(int position, out int openIndex) {
      openIndex = FindIndexOfBraceAtOrAfter(position);
      if ( openIndex >= 0 ) {
        BracePos pos = this.braces[openIndex];
        if ( IsOpeningBrace(pos.Brace) && pos.Position == position ) {
          return pos;
        }
      }
      return FindClosestOpeningBrace(position, out openIndex);
    }

    private BracePos? FindClosestOpeningBrace(int position, out int openIndex) {
      openIndex = FindIndexOfBraceBefore(position);
      if ( openIndex < 0 ) {
        return null;
      }
      int pairs = 0;
      while ( openIndex >= 0 ) {
        BracePos current = this.braces[openIndex];
        if ( !IsOpeningBrace(current.Brace) ) {
          pairs++;
        } else if ( pairs == 0 ) {
          return current;
        } else {
          pairs--;
        }
        openIndex--;
      }
      return null;
    }

    // We don't want to parse the document in small spans
    // as it is too expensive, so force a larger span if
    // necessary. However, if we've already parsed
    // beyond the span, leave it be
    private void EnsureLinesInPreferredSpan(SnapshotSpan span) {
      int minSpanLen = Math.Max(100, (int)(span.Snapshot.Length * 0.10));
      var realSpan = span;
      int lastPosition = this.LastParsedPosition;

      var snapshot = this.Snapshot;
      if ( lastPosition > 0 && lastPosition >= span.End ) {
        // already parsed this, so no need to do it again
        return;
      }
      int parseFrom = lastPosition + 1;
      int parseUntil = Math.Min(
        snapshot.Length, 
        Math.Max(span.End, parseFrom + minSpanLen)
        );

      ContinueParsing(parseFrom, parseUntil);
    }

    private void ContinueParsing(int parseFrom, int parseUntil) {
      int startPosition = 0;
      int lastGoodBrace = 0;
      int lastState = 0;
      // figure out where to start parsing again
      Stack<BracePos> pairs = new Stack<BracePos>();
      for ( int i=0; i < braces.Count; i++ ) {
        BracePos r = braces[i];
        if ( r.Position > parseFrom ) break;
        if ( IsOpeningBrace(r.Brace) ) {
          pairs.Push(r);
        } else if ( pairs.Count > 0 ) {
          pairs.Pop();
        }
        startPosition = r.Position + 1;
        lastGoodBrace = i;
        lastState = r.State;
      }
      if ( lastGoodBrace < braces.Count - 1 ) {
        braces.RemoveRange(lastGoodBrace+1, braces.Count - lastGoodBrace - 1);
      }

      ExtractBraces(pairs, startPosition, parseUntil, lastState);
    }

    private void ExtractBraces(Stack<BracePos> pairs, int startOffset, int endOffset, int state) {
      braceScanner.Reset(state);
      int lineNum = Snapshot.GetLineNumberFromPosition(startOffset);
      while ( lineNum < Snapshot.LineCount  ) {
        var line = Snapshot.GetLineFromLineNumber(lineNum++);
        var lineOffset = startOffset > 0 ? startOffset - line.Start : 0;
        if ( line.Length != 0 ) {
          ExtractFromLine(pairs, line, lineOffset);
        }
        startOffset = 0;
        this.LastParsedPosition = line.End;
        if ( line.End >= endOffset ) break;
      }
    }

    private void ExtractFromLine(Stack<BracePos> pairs, ITextSnapshotLine line, int lineOffset) {
      var lc = new LineChars(line, lineOffset);
      CharPos cp = CharPos.Empty;
      while ( !lc.EndOfLine ) {
        if ( !this.braceScanner.Extract(lc, ref cp) )
          continue;
        if ( IsOpeningBrace(cp) ) {
          BracePos p = cp.AsBrace(pairs.Count);
          pairs.Push(p);
          Add(p);
          // we don't need to check if it's a closing brace
          // because the extractor won't return anything else
        } else if ( pairs.Count > 0 ) {
          BracePos p = pairs.Peek();
          if ( braceList[p.Brace] == cp.Char ) {
            // yield closing brace
            pairs.Pop();
            BracePos c = cp.AsBrace(p.Depth);
            Add(c);
          } else {
            // closing brace does not correspond
            // to opening brace at same depth
            this.braceErrors.Add(cp);
          }
        } else {
          // closing brace has no opening brace
          this.braceErrors.Add(cp);
        }
      }
      this.LastParsedPosition = line.End;
    }

    private void Add(BracePos brace) {
      braces.Add(brace);
      LastParsedPosition = brace.Position;
    }


    // simple binary-search like for the closest 
    // brace to this position
    private int FindIndexOfBraceAtOrAfter(int position) {
      int first = 0;
      int last = this.braces.Count - 1;
      int candidate = -1;
      while ( first <= last ) {
        int mid = (first + last) / 2;
        BracePos midPos = braces[mid];
        if ( midPos.Position < position ) {
          // keep looking in second half
          first = mid + 1;
        } else if ( midPos.Position > position ) {
          // keep looking in first half
          candidate = mid;
          last = mid - 1;
        } else {
          // we've got an exact match
          candidate = mid;
          break;
        }
      }
      return candidate;
    }
    private int FindIndexOfBraceBefore(int position) {
      int first = 0;
      int last = this.braces.Count - 1;
      int candidate = -1;
      while ( first <= last ) {
        int mid = (first + last) / 2;
        BracePos midPos = braces[mid];
        if ( midPos.Position < position ) {
          // keep looking in second half
          candidate = mid;
          first = mid + 1;
        } else if ( midPos.Position > position ) {
          // keep looking in first half
          last = mid - 1;
        } else {
          // we've got an exact match
          // but we're interested on an strict
          // order, so return the item before this one
          candidate = mid - 1;
          break;
        }
      }
      return candidate;
    }


    private void InvalidateFromBraceAtIndex(ITextSnapshot snapshot, int index) {
      if ( index < braces.Count ) {
        // invalidate the brace list
        braces.RemoveRange(index, braces.Count - index);
      }

      if ( braces.Count > 0 ) {
        this.LastParsedPosition = braces[braces.Count - 1].Position;
      } else {
        this.LastParsedPosition = -1;
      }
    }

    private void InvalidateBraceErrorsFromPos(int position) {
      // invalidate brace errors
      int lastPos = -1;
      for ( int i = 0; i < this.braceErrors.Count; i++ ) {
        lastPos = i;
        CharPos ch = this.braceErrors[i];
        if ( ch.Position >= position )
          break;
      }
      if ( lastPos >= 0 && lastPos < braceErrors.Count ) {
        braceErrors.RemoveRange(lastPos, braceErrors.Count - lastPos);
      }
    }

    private bool IsOpeningBrace(char ch) {
      // linear search will be better with so few entries
      var keys = braceList.Keys;
      for ( int i = 0; i < keys.Count; i++ ) {
        if ( keys[i] == ch ) return true;
      }
      return false;
    }

    private bool ScanIsUnnecessary() {
      return this.language == null
          || String.IsNullOrEmpty(this.BraceChars);
    }
  }
}
