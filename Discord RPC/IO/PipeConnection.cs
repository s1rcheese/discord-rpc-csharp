﻿using DiscordRPC.Logging;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace DiscordRPC.IO
{
	//TODO: Make Internal
	public class PipeConnection : IDisposable
	{
		/// <summary>
		/// Discord Pipe Name
		/// </summary>
		const string PIPE_NAME = @"discord-ipc-{0}";
		private NamedPipeClientStream _stream;

		public int ConnectedPipe { get; private set; }

		public bool IsConnected { get { return _isconnected; } }
		private bool _isconnected = false;

		public ILogger Logger { get { return _logger; } set { _logger = value; } }
		public ILogger _logger = new NullLogger();

		#region Pipe Management

		/// <summary>
		/// Attempts to establish a connection to the Discord Client
		/// </summary>
		/// <param name="pipe">The pipe the discord client is located on. Set to -1 for any available pipe.</param>
		/// <returns></returns>
		public bool AttemptConnection(int pipe)
		{
			if (pipe < 0)
			{
				//Iterate over each pipe, trying to connect. If we connect, end the loop and return true.
				for (int i = 0; i < 10; i++)
					if (CreateConnection(i)) return true;

				//We failed to conect, so return false
				return false;
			}
			else
			{
				//Attempt to connect to the target pipe
				return CreateConnection(pipe);
			}
		}

		private bool CreateConnection(int pipe)
		{
			//Prepare the pipe name
			string pipename = string.Format(PIPE_NAME, pipe);
			Logger.Info("Attempting to connect to " + pipename);

			try
			{
				//Create the client
				_stream = new NamedPipeClientStream(".", pipename, PipeDirection.InOut, PipeOptions.Asynchronous);
				_stream.Connect(1000);
				
				//Spin for a bit while we wait for it to finish connecting
				Logger.Info("Waiting for connection...");
				do { Thread.Sleep(250); } while (!_stream.IsConnected);

				//Store the value
				Logger.Info("Connected to " + pipename);
				ConnectedPipe = pipe;
				_isconnected = true;
				return true;
			}
			catch (Exception e)
			{
				//Something happened, try again
				//TODO: Log the failure condition
				Logger.Error("Failed connection to {0}. {1}", pipename, e.Message);
				_isconnected = false;
				_stream = null;
			}

			//We are succesfull if the stream isn't null
			return _stream == null;
		}
		#endregion
		
		#region Frame Write

		/// <summary>
		/// Writes the handshake to the connection
		/// </summary>
		/// <param name="version">Version of the IPC protocol</param>
		/// <param name="client">The client ID</param>
		/// <returns></returns>
		public bool WriteHandshake(int version, string client)
		{
			PipeFrame frame = new PipeFrame();
			frame.SetObject(Opcode.Handshake, new Handshake() { Version = version, ClientID = client });

			return WriteFrame(frame);
		}

		/// <summary>
		/// Writes the frame to the connection
		/// </summary>
		/// <param name="frame"></param>
		/// <returns></returns>
		public bool WriteFrame(PipeFrame frame)
		{
			//u_stream is multithread friendly, so we can just write directly
			bool success =  Write(frame);
			//_stream.WaitForPipeDrain();

			return success;
		}

		#endregion

		#region IO Operation
		
		#region Read
		public bool TryReadFrame(out PipeFrame frame)
		{
			//Set the pipe frame to default
			frame = default(PipeFrame);
			
			//Try to read the values
			uint op;
			if (!TryReadUInt32(out op))
			{
				Logger.Error("Bad OpCode");
				return false;
			}

			uint len;
			if (!TryReadUInt32(out len))
			{
				Logger.Error("Bad Length");
				return false;
			}


			//Read the data. This could potentially cause issues if we ever get anything greater than a int.
			//TODO: Better implementation of this read using uints
			byte[] buff = new byte[len];
			int bytesread = Read(buff, (int)len);

			if (bytesread != len)
			{
				Logger.Error("Bad Data");
				return false;
			}

			//Create the frame
			frame = new PipeFrame()
			{
				Opcode = (Opcode)op,
				Data = buff
			};

			//Success!
			return true;
		}

		private int Read(byte[] buff, int length) { return _stream.Read(buff, 0, length); }
		private bool TryReadUInt32(out uint value)
		{
			//Read the bytes
			byte[] bytes = new byte[4];
			int cnt = Read(bytes, 4);
			if (cnt != 4)
			{
				Logger.Error("Did not ready 4 bytes!");
				value = 0;
				return false;
			}

			//Convert to int
			if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
			value = BitConverter.ToUInt32(bytes, 0);
			return true;
		}
		#endregion

		#region Write
		private bool Write(PipeFrame frame)
		{
			//Get all the bytes
			byte[] op = ConvertBytes((uint)frame.Opcode);
			byte[] len = ConvertBytes(frame.Length);
			byte[] data = frame.Data;

			//Copy it all into a buffer
			byte[] buffer = new byte[op.Length + len.Length + data.Length];
			op.CopyTo(buffer, 0);
			len.CopyTo(buffer, op.Length);
			data.CopyTo(buffer, op.Length + len.Length);

			//Write it to the stream
			_stream.Write(buffer, 0, buffer.Length);

			return true;
		}
		
		/// <summary>
		/// Gets the bytes of a uint32 value in LE format.
		/// </summary>
		/// <param name="uint32"></param>
		/// <returns></returns>
		private byte[] ConvertBytes(uint uint32)
		{
			byte[] bytes = BitConverter.GetBytes(uint32);

			//If we are already in LE, we dont need to flip it
			if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);

			//Give back the bytes
			return bytes;
		}

		#endregion
		#endregion

		/// <summary>
		/// Closes the pipe (but does not dispose of this object).
		/// </summary>
		public void Close()
		{
			if (_stream != null)
			{
				Logger.Info("Dispoing stream...");
				_stream.Dispose();
			}
		}

		/// <summary>
		/// Disposes the pipe and this object.
		/// </summary>
		public void Dispose()
		{
			//Abort the thread. The thread will manage everything else automatically
			if (_stream != null)
			{
				_stream.Dispose();
				_stream = null;
				_isconnected = false;
			}
		}
	}
}
