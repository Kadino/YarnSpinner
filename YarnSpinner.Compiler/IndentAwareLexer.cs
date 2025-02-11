namespace Yarn.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Antlr4.Runtime;

    /// <summary>
    /// A Lexer subclass that detects newlines and generates indent and
    /// dedent tokens accordingly.
    /// </summary>
    public abstract class IndentAwareLexer : Lexer
    {
        /// <summary>
        /// A stack keeping track of the levels of indentations we have
        /// seen so far.
        /// </summary>
        private readonly Stack<int> indents = new Stack<int>();

        /// <summary>
        /// The collection of tokens that we have seen, but have not yet
        /// returned. This is needed when NextToken encounters a newline,
        /// which means we need to buffer indents or dedents. NextToken
        /// only returns a single <see cref="IToken"/> at a time, which
        /// means we use this list to buffer it.
        /// </summary>
        private readonly Queue<IToken> pendingTokens = new Queue<IToken>();

        /// <summary>
        /// The collection of <see cref="Warning"/> objects we've
        /// generated.
        /// </summary>
        private readonly List<Warning> warnings = new List<Warning>();

        /// <summary>
        /// Initializes a new instance of the <see
        /// cref="IndentAwareLexer"/> class.
        /// </summary>
        /// <param name="input">The incoming character stream.</param>
        /// <param name="output">The <see cref="TextWriter"/> to send
        /// output to.</param>
        /// <param name="errorOutput">The <see cref="TextWriter"/> to send
        /// errors to.</param>
        public IndentAwareLexer(ICharStream input, TextWriter output, TextWriter errorOutput)
        : base(input, output, errorOutput)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see
        /// cref="IndentAwareLexer"/> class.
        /// </summary>
        /// <param name="input">The incoming character stream.</param>
        protected IndentAwareLexer(ICharStream input)
        : base(input)
        {
        }

        /// <summary>
        /// Gets the collection of warnings determined during lexing.
        /// </summary>
        public IEnumerable<Warning> Warnings { get => this.warnings; }

        /// <inheritdoc/>
        public override IToken NextToken()
        {
            if (this.HitEOF && this.pendingTokens.Count > 0)
            {
                // We have hit the EOF, but we have tokens still pending.
                // Start returning those tokens.
                return this.pendingTokens.Dequeue();
            }
            else if (this.InputStream.Size == 0)
            {
                // There's no more incoming symbols, and we don't have
                // anything pending, so we've hit the end of the file.
                this.HitEOF = true;

                // Return the EOF token.
                return new CommonToken(Eof, "<EOF>");
            }
            else
            {
                // Get the next token, which will enqueue one or more new
                // tokens into the pending tokens queue.
                this.CheckNextToken();

                if (this.pendingTokens.Count > 0)
                {
                    // Then, return a single token from the queue.
                    return this.pendingTokens.Dequeue();
                }
                else
                {
                    // Nothing left in the queue. Return null.
                    return null;
                }
            }
        }

        private void CheckNextToken()
        {
            var currentToken = base.NextToken();

            switch (currentToken.Type)
            {
                case YarnSpinnerLexer.NEWLINE:
                    // Insert indents or dedents depending on the next
                    // token's indentation, and enqueues the newline at the
                    // correct place
                    this.HandleNewLineToken(currentToken);
                    break;
                case Eof:
                    // Insert dedents before the end of the file, and then
                    // enqueues the EOF.
                    this.HandleEndOfFileToken(currentToken);
                    break;
                default:
                    this.pendingTokens.Enqueue(currentToken);
                    break;
            }
        }

        private void HandleEndOfFileToken(IToken currentToken)
        {
            // We're at the end of the file. Emit as many dedents as we
            // currently have on the stack.
            while (this.indents.Count > 0)
            {
                var indent = this.indents.Pop();
                this.InsertToken($"<dedent: {indent}>", YarnSpinnerLexer.DEDENT);
            }

            // Finally, enqueue the EOF token.
            this.pendingTokens.Enqueue(currentToken);
        }

        private void HandleNewLineToken(IToken currentToken)
        {
            // We're about to go to a new line. Look ahead to see how
            // indented it is.

            // insert the current NEWLINE token
            this.pendingTokens.Enqueue(currentToken);

            int currentIndentationLength = this.GetLengthOfNewlineToken(currentToken);

            int previousIndent;
            if (this.indents.Count > 0)
            {
                previousIndent = this.indents.Peek();
            }
            else
            {
                previousIndent = 0;
            }

            if (currentIndentationLength > previousIndent)
            {
                // We are more indented on this line than on the previous
                // line. Insert an indentation token, and record the new
                // indent level.
                this.indents.Push(currentIndentationLength);

                this.InsertToken($"<indent to {currentIndentationLength}>", YarnSpinnerLexer.INDENT);
            }
            else if (currentIndentationLength < previousIndent)
            {
                // We are less indented on this line than on the previous
                // line. For each level of indentation we're now lower
                // than, insert a dedent token and remove that indentation
                // level.
                while (currentIndentationLength < previousIndent)
                {
                    // Remove this indent from the stack and generate a
                    // dedent token for it.
                    previousIndent = this.indents.Pop();
                    this.InsertToken($"<dedent from {previousIndent}>", YarnSpinnerLexer.DEDENT);

                    // Figure out the level of indentation we're on -
                    // either the top of the indent stack (if we have any
                    // indentations left), or zero.
                    if (this.indents.Count > 0)
                    {
                        previousIndent = this.indents.Peek();
                    }
                    else
                    {
                        previousIndent = 0;
                    }
                }
            }
        }

        // Given a NEWLINE token, return the length of the indentation
        // following it by counting the spaces and tabs after it.
        private int GetLengthOfNewlineToken(IToken currentToken)
        {
            if (currentToken.Type != YarnSpinnerLexer.NEWLINE)
            {
                throw new ArgumentException($"{nameof(this.GetLengthOfNewlineToken)} expected {nameof(currentToken)} to be a {nameof(YarnSpinnerLexer.NEWLINE)} ({YarnSpinnerLexer.NEWLINE}), not {currentToken.Type}");
            }

            int length = 0;
            bool sawSpaces = false;
            bool sawTabs = false;

            foreach (char c in currentToken.Text)
            {
                switch (c)
                {
                    case ' ':
                        length += 1;
                        sawSpaces = true;
                        break;
                    case '\t':
                        sawTabs = true;
                        length += 8;
                        break;
                }
            }

            if (sawSpaces && sawTabs)
            {
                this.warnings.Add(new Warning { Token = currentToken, Message = "Indentation contains tabs and spaces" });
            }

            return length;
        }

        /// <summary>
        /// Inserts a new token with the given text and type, as though it
        /// had appeared in the input stream.
        /// </summary>
        /// <param name="text">The text to use for the token.</param>
        /// <param name="type">The type of the token.</param>
        /// <remarks>The token will have a zero length.</remarks>
        private void InsertToken(string text, int type)
        {
            // ***
            // https://www.antlr.org/api/Java/org/antlr/v4/runtime/Lexer.html#_tokenStartCharIndex
            int startIndex = this.TokenStartCharIndex + this.Text.Length;
            this.InsertToken(startIndex, startIndex - 1, text, type, this.Line, this.Column);
        }

        private void InsertToken(int startIndex, int stopIndex, string text, int type, int line, int column)
        {
            var tokenFactorySourcePair = Tuple.Create((ITokenSource)this, (ICharStream)this.InputStream);

            CommonToken token = new CommonToken(tokenFactorySourcePair, type, YarnSpinnerLexer.DefaultTokenChannel, startIndex, stopIndex)
            {
                Text = text,
                Line = line,
                Column = column,
            };

            this.pendingTokens.Enqueue(token);
        }

        /// <summary>
        /// A warning emitted during lexing.
        /// </summary>
        public struct Warning
        {
            /// <summary>
            /// The token associated with the warning.
            /// </summary>
            public IToken Token;

            /// <summary>
            /// The message associated with the warning.
            /// </summary>
            public string Message;
        }
    }
}
