﻿using System;
using System.Collections.Generic;

namespace Yarn
{
	internal class VirtualMachine
	{

		public delegate void LineHandler(Dialogue.LineResult line);
		public delegate void OptionsHandler(Dialogue.OptionSetResult options);
		public delegate void CommandHandler(Dialogue.CommandResult command);
		public delegate void NodeCompleteHandler(Dialogue.NodeCompleteResult complete);

		internal VirtualMachine (Dialogue d, Program p)
		{
			program = p;
			dialogue = d;
			state = new State ();
		}

		void Reset() {
			state = new State();
		}

		public LineHandler lineHandler;
		public OptionsHandler optionsHandler;
		public CommandHandler commandHandler;
		public NodeCompleteHandler nodeCompleteHandler;

		private Dialogue dialogue;

		private Program program;
		private State state;

		public string currentNode { get { return state.currentNode; } }


		public enum ExecutionState {
			Stopped,
			WaitingOnOptionSelection,
			Running
		}

		public ExecutionState executionState { get; private set; }

		IList<Instruction> currentNodeInstructions;

		public void SetNode(string nodeName) {
			if (program.nodes.ContainsKey(nodeName) == false) {
				dialogue.LogErrorMessage("No node named " + nodeName);
				executionState = ExecutionState.Stopped;
				return;
			}

			dialogue.LogDebugMessage ("Running node " + nodeName);

			currentNodeInstructions = program.nodes [nodeName].instructions;
			state.currentNode = nodeName;
			state.programCounter = 0;
			state.ClearStack ();
		}

		public void Stop() {
			executionState = ExecutionState.Stopped;
		}

		// Executes the next instruction in the current node.
		internal void RunNext() {

			if (executionState == ExecutionState.WaitingOnOptionSelection) {
				dialogue.LogErrorMessage ("Cannot continue running dialogue. Still waiting on option selection.");
				return;
			}

			if (executionState == ExecutionState.Stopped)
				executionState = ExecutionState.Running;

			Instruction currentInstruction = currentNodeInstructions [state.programCounter];

			RunInstruction (currentInstruction);

			state.programCounter++;

			if (state.programCounter >= currentNodeInstructions.Count) {
				executionState = ExecutionState.Stopped;
				nodeCompleteHandler (new Dialogue.NodeCompleteResult (null));
				dialogue.LogDebugMessage ("Run complete.");
			}

		}

		// Looks up the instruction number for a named label in the current node.
		internal int FindInstructionPointForLabel(string labelName) {
			int i = 0;

			foreach (Instruction instruction in currentNodeInstructions) {

				// If this instruction is a label and it has the desired
				// name, return its position in the node
				if (instruction.operation == ByteCode.Label && 
					(string)instruction.operandA == labelName) {
					return i;
				}
				i++;
					
			}

			// Couldn't find the node..
			throw new IndexOutOfRangeException ("Unknown label " + 
				labelName + " in node " + state.currentNode);
		}



		internal void RunInstruction(Instruction i) {
			switch (i.operation) {
			case ByteCode.Label:
				
				// No-op; used as a destination for JumpTo and Jump.
				break;
			case ByteCode.JumpTo:

				// Jumps to a named label
				state.programCounter = FindInstructionPointForLabel ((string)i.operandA);

				break;
			case ByteCode.RunLine:

				// Looks up a string from the string table and
				// passes it to the client as a line

				var lineText = program.GetString ((int)i.operandA);

				lineHandler (new Dialogue.LineResult (lineText));
				
				break;
			case ByteCode.RunCommand:

				// Passes a string to the client as a custom command
				commandHandler (
					new Dialogue.CommandResult ((string)i.operandA)
				);

				break;
			case ByteCode.PushString:

				// Pushes a string value onto the stack; the operand
				// is an index into the string table, so that's looked up
				// first.
				state.PushValue (program.GetString ((int)i.operandA));

				break;
			case ByteCode.PushNumber:

				// Pushes a number onto the stack.
				state.PushValue ((float)i.operandA);

				break;
			case ByteCode.PushBool:

				// Pushes a boolean value onto the stack.
				state.PushValue ((bool)i.operandA);

				break;
			case ByteCode.PushNull:

				// Pushes a null value onto the stack.
				state.PushValue (new Value ());

				break;
			case ByteCode.JumpIfFalse:

				// Jumps to a named label if the value on the top of the stack
				// evaluates to the boolean value 'false'.
				if (state.PeekValue ().AsBool == false) {
					state.programCounter = FindInstructionPointForLabel ((string)i.operandA);
				}
				break;
			
			case ByteCode.Jump:

				// Jumps to a label whose name is on the stack.
				var jumpDestination = state.PeekValue ().AsString;
				state.programCounter = FindInstructionPointForLabel (jumpDestination);

				break;
			
			case ByteCode.Pop:

				// Pops a value from the stack.
				state.PopValue ();
				break;

			case ByteCode.CallFunc:

				// Call a function, whose parameters are expected to
				// be on the stack. Pushes the function's return value,
				// if it returns one.
				var functionName = (string)i.operandA;
				var function = dialogue.library.GetFunction (functionName);
				{
					// Get the parameters, which were pushed in reverse
					Value[] parameters = new Value[function.paramCount];
					for (int param = function.paramCount - 1; param >= 0; param--) {
						parameters [param] = state.PopValue ();
					}

					// Invoke the function
					var result = function.InvokeWithArray (parameters);

					// If the function returns a value, push it
					if (function.returnsValue) {
						state.PushValue (result);
					}
				}

				break;
			case ByteCode.PushVariable:

				// Get the contents of a variable, push that onto the stack.
				var variableName = (string)i.operandA;
				var loadedValue = dialogue.continuity.GetNumber (variableName);
				state.PushValue (loadedValue);

				break;
			case ByteCode.StoreVariable:

				// Store the top value on the stack in a variable.
				var topValue = state.PeekValue ();
				var destinationVariableName = (string)i.operandA;

				if (topValue.type == Value.Type.Number) {
					dialogue.continuity.SetNumber (destinationVariableName, topValue.AsNumber);
				} else {
					throw new NotImplementedException ("Only numbers can be stored in variables.");
				}

				break;
			case ByteCode.Stop:

				// Immediately stop execution, and report that fact.
				executionState = ExecutionState.Stopped;
				nodeCompleteHandler (new Dialogue.NodeCompleteResult (null));

				break;
			case ByteCode.RunNode:

				// Get a string from the stack, and jump to a node with that name.
				var nodeName = state.PeekValue ().AsString;
				SetNode (nodeName);

				break;
			case ByteCode.AddOption:

				// Add an option to the current state.
				state.currentOptions.Add (new KeyValuePair<int, string> ((int)i.operandA, (string)i.operandB));


				break;
			case ByteCode.ShowOptions:

				// If we have a single option, and it has no label, select it immediately and continue
				// execution
				if (state.currentOptions.Count == 1 && state.currentOptions[0].Key == -1) {
					var destinationNode = state.currentOptions[0].Value;
					state.PushValue(destinationNode);
					state.currentOptions.Clear();
					break;
				}

				// Otherwise, present the list of options to the user and let them pick
				var optionStrings = new List<string> ();
				foreach (var option in state.currentOptions) {
					optionStrings.Add (program.GetString (option.Key));
				}

				// We can't continue until our client tell us which option to pick
				executionState = ExecutionState.WaitingOnOptionSelection;

				// Pass the options set to the client, as well as a delegate for them to call when the
				// user has made a selection
				optionsHandler (new Dialogue.OptionSetResult (optionStrings, delegate (int selectedOption) {

					// we now know what number option was selected; push the corresponding node name
					// to the stack
					var destinationNode = state.currentOptions[selectedOption].Value;
					state.PushValue(destinationNode);

					// We no longer need the accumulated list of options; clear it so that it's
					// ready for the next one
					state.currentOptions.Clear();

					// We can now also keep running
					executionState = ExecutionState.Running;

				}));

				break;
			default:

				// Whoa, no idea what bytecode this is. Stop the program
				// and throw an exception.
				executionState = ExecutionState.Stopped;
				throw new ArgumentOutOfRangeException ();
			}
		}

		
	}
}

