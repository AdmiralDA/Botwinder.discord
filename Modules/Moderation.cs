﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Botwinder.core;
using Botwinder.entities;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using guid = System.UInt64;

namespace Botwinder.modules
{
	public class Moderation: IModule
	{
		private const string ErrorUnknownString = "Unknown error, please poke <@{0}> to take a look x_x";
		private const string ErrorPermissionsString = "I don't have necessary permissions.";
		private const string ErrorPermissionHierarchyString = "Something went wrong, I may not have server permissions to do that.\n(Hint: Botwinder has to be above other roles to be able to manage them: <http://i.imgur.com/T8MPvME.png>)";
		private const string BanPmString = "Hello!\nI regret to inform you, that you have been **banned {0} on the {1} server** for the following reason:\n{2}";
		private const string BanNotFoundString = "I couldn't find them :(";
		private const string BanConfirmString = "_\\*fires them railguns at {0}*_  Ò_Ó";
		private const string UnbanConfirmString = "I've unbanned {0}... ó_ò";
		private const string KickArgsString = "I'm supposed to shoot... who?\n";
		private const string KickNotFoundString = "I couldn't find them :(";
		private const string KickPmString = "Hello!\nYou have been kicked out of the **{0} server** by its Moderators for the following reason:\n{1}";
		private const string WarningNotFoundString = "I couldn't find them :(";
		private const string WarningPmString = "Hello!\nYou have been issued a formal **warning** by the Moderators of the **{0} server** for the following reason:\n{1}";
		private const string MuteIgnoreChannelString = "{0}, you've been muted.";
		private const string MuteConfirmString = "*Silence!!  ò_ó\n...\nI keel u, {0}!!*  Ò_Ó";
		private const string MuteChannelConfirmString = "Silence!!  ò_ó";
		private const string UnmuteConfirmString = "Speak {0}!";
		private const string UnmuteChannelConfirmString = "You may speak now.";
		private const string MuteNotFoundString = "And who would you like me to ~~kill~~ _silence_?";
		private const string InvalidArgumentsString = "Invalid arguments.\n";
		private const string RoleNotFoundString = "The Muted role is not configured - head to <http://botwinder.info/config>";
		private const string TempChannelConfirmString = "Here you go! <3\n_(Temporary channel `{0}` was created.)_";

		private BotwinderClient Client;

		private TimeSpan TempChannelDelay = TimeSpan.FromMinutes(3);


		public Func<Exception, string, guid, Task> HandleException{ get; set; }

		public List<Command> Init(IBotwinderClient iClient)
		{
			this.Client = iClient as BotwinderClient;
			List<Command> commands = new List<Command>();

			this.Client.Events.BanUser += Ban;
			this.Client.Events.BanUsers += Ban;
			this.Client.Events.UnBanUsers += UnBan;
			this.Client.Events.MuteUser += Mute;
			this.Client.Events.MuteUsers += Mute;
			this.Client.Events.UnMuteUsers += UnMute;

			this.Client.Events.UserJoined += OnUserJoined;


// !clear
			Command newCommand = new Command("clear");
			newCommand.Type = CommandType.Operation;
			newCommand.Description = "Deletes specified amount of messages (within two weeks.) If you mention someone as well, it will remove only their messages. Use with paremeters: _[@users] n_ - optional _@user_ mentions or ID's (this parameter has to be first, if specified.) And mandatory _n_ parameter, the count of how many messages to remove.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( !e.Server.Guild.CurrentUser.GuildPermissions.ManageMessages )
				{
					await e.Message.Channel.SendMessageSafe(ErrorPermissionsString);
					return;
				}

				int n = 0;
				RestUserMessage msg = null;
				List<guid> userIDs = this.Client.GetMentionedUserIds(e);

				if( userIDs.Count == 0 && e.MessageArgs != null && (e.MessageArgs.Length > 1 || (e.MessageArgs.Length == 1 && e.Command.Id == "nuke")) )
				{
					await e.Message.Channel.SendMessageSafe("I can see that you're trying to use more parameters, but I did not find any IDs or mentions.");
					return;
				}

				bool clearLinks = e.Command.Id.ToLower() == "clearlinks";
				if( clearLinks && userIDs.Any() )
				{
					//todo - why not?
					await e.Message.Channel.SendMessageSafe($"`{e.Server.Config.CommandPrefix}clearLinks` does not take `@user` mentions as parameter.");
					return;
				}

				if( e.Command.Id == "nuke" )
				{
					n = int.MaxValue - 1;
					if( userIDs.Count > 0 )
						msg = await e.Message.Channel.SendMessageAsync("Deleting all the messages by specified users.");
					else
						msg = await e.Message.Channel.SendMessageAsync("Nuking the channel, I'll tell you when I'm done (large channels may take up to half an hour...)");
				}
				else if( e.MessageArgs == null || e.MessageArgs.Length < 1 || !int.TryParse(e.MessageArgs[e.MessageArgs.Length - 1], out n) )
				{
					await e.Message.Channel.SendMessageSafe("Please tell me how many messages should I delete!");
					return;
				}
				else if( userIDs.Count > 0 )
				{
					msg = await e.Message.Channel.SendMessageAsync("Deleting " + n.ToString() + " messages by specified users.");
				}
				else
					msg = await e.Message.Channel.SendMessageAsync("Deleting " + (clearLinks ? "attachments and embeds in " : "") + n.ToString() + " messages.");

				int userCount = userIDs.Count();
				guid lastRemoved = e.Message.Id;

				bool IsWithinTwoWeeks(IMessage m){
					if( DateTime.UtcNow - Utils.GetTimeFromId(m.Id) < TimeSpan.FromDays(13.9f) )
						return true;
					return false;
				}

				List<guid> idsToDelete = new List<guid>();

				bool canceled = await e.Operation.While(() => n > 0, async () => {
					IMessage[] messages = null;

					try
					{
						messages = (await e.Message.Channel.GetMessagesAsync(lastRemoved, Direction.Before, 100, CacheMode.AllowDownload).Flatten()).ToArray();
					}
					catch(Exception exception)
					{
						await this.Client.LogException(exception, e);
						lastRemoved = 0;
						return true;
					}

					List<guid> ids = null;
					if( messages == null || messages.Length == 0 ||
						(clearLinks && userCount == 0 && !(ids = messages.TakeWhile(IsWithinTwoWeeks).Where(m => (m.Attachments != null && m.Attachments.Any()) || (m.Embeds != null && m.Embeds.Any())).Select(m => m.Id).ToList()).Any()) ||
						(!clearLinks && userCount == 0 && !(ids = messages.TakeWhile(IsWithinTwoWeeks).Select(m => m.Id).ToList()).Any()) ||
						(userCount > 0 && !(ids = messages.TakeWhile(IsWithinTwoWeeks).Where(m => (m?.Author != null && userIDs.Contains(m.Author.Id))).Select(m => m.Id).ToList()).Any()) )
					{
						lastRemoved = e.Message.Id;
						return true;
					}

					if( ids.Count > n )
						ids = ids.Take(n).ToList();

					n -= ids.Count;
					if( messages.Length < 100 ) //this was the last pull
						n = 0;

					idsToDelete.AddRange(ids);
					lastRemoved = ids.Last();

					return false;
				});

				if( canceled )
					return;

				if( !this.Client.ClearedMessageIDs.ContainsKey(e.Server.Id) )
					this.Client.ClearedMessageIDs.Add(e.Server.Id, new List<guid>());

				this.Client.ClearedMessageIDs[e.Server.Id].AddRange(idsToDelete);

				int i = 0;
				guid[] chunk = new guid[100];
				guid[] idsToDeleteArray = idsToDelete.ToArray();
				canceled = await e.Operation.While(() => i < (idsToDeleteArray.Length + 99) / 100, async () => {
					try
					{
						if( idsToDeleteArray.Length <= 100 )
							await e.Channel.DeleteMessagesAsync(idsToDeleteArray);
						else
						{
							int chunkSize = idsToDeleteArray.Length - (100 * i);
							Array.Copy(idsToDeleteArray, i * 100, chunk, 0, Math.Min(chunkSize, 100));
							if( chunkSize < 100 )
								Array.Resize(ref chunk, chunkSize);
							await e.Channel.DeleteMessagesAsync(chunk);
						}
						i++;
					}
					catch(Discord.Net.HttpException) { }
					catch(Exception exception)
					{
						await this.Client.LogException(exception, e);
						return true;
					}

					return false;
				});

				if( canceled )
					return;

				try
				{
					if( lastRemoved == 0 )
						await msg.ModifyAsync(m => m.Content = "There was an error while downloading messages, you can try again but if it doesn't work, then it's a bug - please tell Rhea :<");
					else
					{
						if( !e.Message.Deleted )
							await e.Message.DeleteAsync();

						await msg.ModifyAsync(m => m.Content = $"~~{msg.Content}~~\n\nDone! _(This message will self-destruct in 10 seconds.)_");
						await Task.Delay(TimeSpan.FromSeconds(10f));
						await msg.ModifyAsync(m => m.Content = "BOOM!!");
						await Task.Delay(TimeSpan.FromSeconds(2f));
						await msg.DeleteAsync();
					}
				}
				catch(Exception) { }
			};
			commands.Add(newCommand);

			newCommand = newCommand.CreateCopy("nuke");
			newCommand.Description = "Nuke the whole channel. You can also mention a user to delete all of their messages. (Within the last two weeks.)";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			commands.Add(newCommand);

			newCommand = newCommand.CreateCopy("clearLinks");
			newCommand.Description = "Delete only messages that contain links. Use with a peremter, a number of messages to delete.";
			commands.Add(newCommand);
			commands.Add(newCommand.CreateAlias("clearlinks"));

// !op
			newCommand = new Command("op");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "_op_ yourself to be able to use `mute`, `kick` or `ban` commands. (Only if configured at <http://botwinder.info/config>)";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				SocketRole role = e.Server.Guild.GetRole(e.Server.Config.OperatorRoleId);
				if( role == null )
				{
					await iClient.SendMessageToChannel(e.Channel, string.Format("I'm really sorry, buuut `{0}op` feature is not configured! Poke your admin to set it up at <http://botwinder.info/config>", e.Server.Config.CommandPrefix));
					return;
				}
				if( !e.Server.Guild.CurrentUser.GuildPermissions.ManageRoles )
				{
					await iClient.SendMessageToChannel(e.Channel, ErrorPermissionsString);
					return;
				}

				string response = "";
				try
				{
					SocketGuildUser user = e.Message.Author as SocketGuildUser;
					if( user.Roles.Any(r => r.Id == e.Server.Config.OperatorRoleId) )
					{
						await user.RemoveRoleAsync(role);
						response = "All done?";
					}
					else
					{
						await user.AddRoleAsync(role);
						response = "Go get em tiger!";
					}
				}
				catch(Exception exception)
				{
					if( exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden )
						response = ErrorPermissionHierarchyString;
					else
						throw;
				}
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

// !mute
			newCommand = new Command("mute");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Temporarily mute mentioned members from the chat. Use with parameters `@user time` where `@user` = user mention(s) or id(s); `time` = duration of the mute (e.g. `7d` or `12h` or `1h30m` - without spaces.); This command has to be configured at <http://botwinder.info/config>!";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( !e.Server.Guild.CurrentUser.GuildPermissions.ManageRoles )
				{
					await e.Message.Channel.SendMessageSafe(ErrorPermissionsString);
					return;
				}

				IRole role = e.Server.Guild.GetRole(e.Server.Config.MuteRoleId);
				if( role == null )
				{
					await e.Message.Channel.SendMessageSafe(RoleNotFoundString);
					return;
				}

				if( e.MessageArgs == null || e.MessageArgs.Length < 2 )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					return;
				}

				SocketRole roleOp = e.Server.Guild.GetRole(e.Server.Config.OperatorRoleId);
				if( roleOp != null && (e.Message.Author as SocketGuildUser).Roles.All(r => r.Id != roleOp.Id) &&
				    !this.Client.IsGlobalAdmin(e.Message.Author.Id) )
				{
					await e.Message.Channel.SendMessageSafe($"`{e.Server.Config.CommandPrefix}op`?");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUsers = this.Client.GetMentionedUsersData(dbContext, e);

				if( mentionedUsers.Count == 0 )
				{
					await iClient.SendMessageToChannel(e.Channel, BanNotFoundString);
					dbContext.Dispose();
					return;
				}

				if( mentionedUsers.Count + 1 > e.MessageArgs.Length )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				int muteDurationMinutes = 0;
				Match dayMatch = Regex.Match(e.MessageArgs[mentionedUsers.Count], "\\d+d", RegexOptions.IgnoreCase);
				Match hourMatch = Regex.Match(e.MessageArgs[mentionedUsers.Count], "\\d+h", RegexOptions.IgnoreCase);
				Match minuteMatch = Regex.Match(e.MessageArgs[mentionedUsers.Count], "\\d+m", RegexOptions.IgnoreCase);

				if( !minuteMatch.Success && !hourMatch.Success && !dayMatch.Success && !int.TryParse(e.MessageArgs[mentionedUsers.Count], out muteDurationMinutes) )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				if( minuteMatch.Success )
					muteDurationMinutes = int.Parse(minuteMatch.Value.Trim('m').Trim('M'));
				if( hourMatch.Success )
					muteDurationMinutes += 60 * int.Parse(hourMatch.Value.Trim('h').Trim('H'));
				if( dayMatch.Success )
					muteDurationMinutes += 24 * 60 * int.Parse(dayMatch.Value.Trim('d').Trim('D'));

				string response = "ò_ó";

				try
				{
					response = await Mute(e.Server, mentionedUsers, TimeSpan.FromMinutes(muteDurationMinutes), role, e.Message.Author as SocketGuildUser);
					dbContext.SaveChanges();
				} catch(Exception exception)
				{
					await this.Client.LogException(exception, e);
					response = string.Format(ErrorUnknownString, this.Client.GlobalConfig.AdminUserId);
				}

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

// !unmute
			newCommand = new Command("unmute");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Unmute previously muted members. This command has to be configured at <http://botwinder.info/config>.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( !e.Server.Guild.CurrentUser.GuildPermissions.ManageRoles )
				{
					await e.Message.Channel.SendMessageSafe(ErrorPermissionsString);
					return;
				}

				IRole role = e.Server.Guild.GetRole(e.Server.Config.MuteRoleId);
				if( role == null || string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUsers = this.Client.GetMentionedUsersData(dbContext, e);

				if( mentionedUsers.Count == 0 )
				{
					await iClient.SendMessageToChannel(e.Channel, MuteNotFoundString);
					dbContext.Dispose();
					return;
				}

				if( mentionedUsers.Count < e.MessageArgs.Length )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				string response = "ó_ò";

				try
				{
					response = await UnMute(e.Server, mentionedUsers, role, e.Message.Author as SocketGuildUser);
					dbContext.SaveChanges();
				} catch(Exception exception)
				{
					await this.Client.LogException(exception, e);
					response = string.Format(ErrorUnknownString, this.Client.GlobalConfig.AdminUserId);
				}

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

// !muteChannel
			newCommand = new Command("muteChannel");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Temporarily mute the current channel. Use with parameter `time` = duration of the mute (e.g. `7d` or `12h` or `1h30m` - without spaces.)";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator;
			newCommand.OnExecute += async e => {
				SocketRole roleOp = e.Server.Guild.GetRole(e.Server.Config.OperatorRoleId);
				if( roleOp != null && (e.Message.Author as SocketGuildUser).Roles.All(r => r.Id != roleOp.Id) &&
				    !this.Client.IsGlobalAdmin(e.Message.Author.Id) )
				{
					await e.Message.Channel.SendMessageSafe($"`{e.Server.Config.CommandPrefix}op`?");
					return;
				}

				string responseString = "Invalid parameters...\n" + e.Command.Description;
				if( e.MessageArgs == null || e.MessageArgs.Length < 1 )
				{
					await this.Client.SendMessageToChannel(e.Channel, responseString);
					return;
				}

				int muteDurationMinutes = 0;
				Match dayMatch = Regex.Match(e.MessageArgs[0], "\\d+d", RegexOptions.IgnoreCase);
				Match hourMatch = Regex.Match(e.MessageArgs[0], "\\d+h", RegexOptions.IgnoreCase);
				Match minuteMatch = Regex.Match(e.MessageArgs[0], "\\d+m", RegexOptions.IgnoreCase);
				if( !minuteMatch.Success && !hourMatch.Success && !dayMatch.Success && !int.TryParse(e.MessageArgs[0], out muteDurationMinutes) )
				{
					await this.Client.SendMessageToChannel(e.Channel, responseString);
					return;
				}

				if( minuteMatch.Success )
					muteDurationMinutes = int.Parse(minuteMatch.Value.Trim('m').Trim('M'));
				if( hourMatch.Success )
					muteDurationMinutes += 60 * int.Parse(hourMatch.Value.Trim('h').Trim('H'));
				if( dayMatch.Success )
					muteDurationMinutes += 24 * 60 * int.Parse(dayMatch.Value.Trim('d').Trim('D'));

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				ChannelConfig channel = dbContext.Channels.FirstOrDefault(c => c.ServerId == e.Server.Id && c.ChannelId == e.Channel.Id);
				if( channel == null )
				{
					channel = new ChannelConfig{
						ServerId = e.Server.Id,
						ChannelId = e.Channel.Id
					};

					dbContext.Channels.Add(channel);
				}

				try
				{
					responseString = await MuteChannel(channel, TimeSpan.FromMinutes(muteDurationMinutes), e.Message.Author as SocketGuildUser);
					dbContext.SaveChanges();
				}
				catch(Exception exception)
				{
					await this.Client.LogException(exception, e);
					responseString = string.Format(ErrorUnknownString, this.Client.GlobalConfig.AdminUserId);
				}
				dbContext.Dispose();

				await this.Client.SendMessageToChannel(e.Channel, responseString);
			};
			commands.Add(newCommand);

// !unmuteChannel
			newCommand = new Command("unmuteChannel");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Mute the current channel temporarily - duration configured at the website.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator;
			newCommand.OnExecute += async e => {
				string responseString = "";
				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				ChannelConfig channel = dbContext.Channels.FirstOrDefault(c => c.ServerId == e.Server.Id && c.ChannelId == e.Channel.Id);
				if( channel == null )
				{
					channel = new ChannelConfig{
						ServerId = e.Server.Id,
						ChannelId = e.Channel.Id
					};

					dbContext.Channels.Add(channel);
				}

				try
				{
					responseString = await UnmuteChannel(channel, e.Message.Author as SocketGuildUser);
					dbContext.SaveChanges();
				}
				catch(Exception exception)
				{
					await this.Client.LogException(exception, e);
					responseString = string.Format(ErrorUnknownString, this.Client.GlobalConfig.AdminUserId);
				}
				dbContext.Dispose();
				await this.Client.SendMessageToChannel(e.Channel, responseString);
			};
			commands.Add(newCommand);

// !kick
			newCommand = new Command("kick");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Use with parameters `@user reason` where `@user` = user mention or id; `reason` = worded description why did you kick them out - they will receive this via PM.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator;
			newCommand.OnExecute += async e => {
				if( !e.Server.Guild.CurrentUser.GuildPermissions.KickMembers )
				{
					await e.Message.Channel.SendMessageSafe(ErrorPermissionsString);
					return;
				}

				SocketRole role = e.Server.Guild.GetRole(e.Server.Config.OperatorRoleId);
				if( role != null && (e.Message.Author as SocketGuildUser).Roles.All(r => r.Id != role.Id) &&
				    !this.Client.IsGlobalAdmin(e.Message.Author.Id) )
				{
					await e.Message.Channel.SendMessageSafe($"`{e.Server.Config.CommandPrefix}op`?");
					return;
				}

				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await iClient.SendMessageToChannel(e.Channel, KickArgsString + e.Command.Description);
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUsers = this.Client.GetMentionedUsersData(dbContext, e);

				if( mentionedUsers.Count == 0 )
				{
					await iClient.SendMessageToChannel(e.Channel, KickNotFoundString);
					dbContext.Dispose();
					return;
				}

				if( mentionedUsers.Count +1 > e.MessageArgs.Length )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				StringBuilder warning = new StringBuilder();
				for(int i = mentionedUsers.Count; i < e.MessageArgs.Length; i++)
				{
					warning.Append(e.MessageArgs[i]);
					warning.Append(" ");
				}

				string response = "";
				List<string> usernames = new List<string>();
				try
				{
					foreach( UserData userData in mentionedUsers )
					{
						SocketGuildUser user = e.Server.Guild.GetUser(userData.UserId);
						if( user == null )
							continue;

						try
						{
							await user.SendMessageSafe(string.Format(KickPmString,
								e.Server.Guild.Name, warning.ToString()));
						}
						catch(Exception) { }
						await Task.Delay(300);

						await user.KickAsync(warning.ToString());
						userData.AddWarning(warning.ToString());
						usernames.Add(user.GetUsername());

						if( this.Client.Events.LogKick != null )
							await this.Client.Events.LogKick(e.Server, user?.GetUsername(), userData.UserId, warning.ToString(), e.Message.Author as SocketGuildUser);
					}
				}
				catch(Exception exception)
				{
					if( exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden )
						response = ErrorPermissionHierarchyString;
					else
						throw;
				}

				if( !usernames.Any() )
				{
					response = "I wasn't able to shoot anyone.";
				}
				else if( string.IsNullOrEmpty(response) )
				{
					response = "I've fired them railguns at " + usernames.ToNames() + ".";
					dbContext.SaveChanges();
				}

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

// !ban
			newCommand = new Command("ban");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Use with parameters `@user time reason` where `@user` = user mention or id; `time` = duration of the ban (e.g. `7d` or `12h` or `0` for permanent.); `reason` = worded description why did you ban them - they will receive this via PM (use `silentBan` to not send the PM)";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 3 )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					return;
				}

				if( !e.Server.Guild.CurrentUser.GuildPermissions.BanMembers )
				{
					await e.Message.Channel.SendMessageSafe(ErrorPermissionsString);
					return;
				}

				SocketRole role = e.Server.Guild.GetRole(e.Server.Config.OperatorRoleId);
				if( role != null && (e.Message.Author as SocketGuildUser).Roles.All(r => r.Id != role.Id) &&
				    !this.Client.IsGlobalAdmin(e.Message.Author.Id) )
				{
					await e.Message.Channel.SendMessageSafe($"`{e.Server.Config.CommandPrefix}op`?");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUsers = this.Client.GetMentionedUsersData(dbContext, e);

				if( mentionedUsers.Count == 0 )
				{
					await iClient.SendMessageToChannel(e.Channel, BanNotFoundString);
					dbContext.Dispose();
					return;
				}

				if( mentionedUsers.Count + 2 > e.MessageArgs.Length )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				StringBuilder warning = new StringBuilder();
				for(int i = mentionedUsers.Count + 1; i < e.MessageArgs.Length; i++)
				{
					warning.Append(e.MessageArgs[i]);
					warning.Append(" ");
				}

				int banDurationHours = 0;
				Match dayMatch = Regex.Match(e.MessageArgs[mentionedUsers.Count], "\\d+d", RegexOptions.IgnoreCase);
				Match hourMatch = Regex.Match(e.MessageArgs[mentionedUsers.Count], "\\d+h", RegexOptions.IgnoreCase);

				if( !hourMatch.Success && !dayMatch.Success && !int.TryParse(e.MessageArgs[mentionedUsers.Count], out banDurationHours) )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				if( hourMatch.Success )
					banDurationHours = int.Parse(hourMatch.Value.Trim('h').Trim('H'));
				if( dayMatch.Success )
					banDurationHours += 24 * int.Parse(dayMatch.Value.Trim('d').Trim('D'));

				string response = "ò_ó";

				try
				{
					response = await Ban(e.Server, mentionedUsers, TimeSpan.FromHours(banDurationHours),
						warning.ToString(), e.Message.Author as SocketGuildUser,
						e.Command.Id.ToLower() == "silentban", e.Command.Id.ToLower() == "purgeban");
					dbContext.SaveChanges();
				} catch(Exception exception)
				{
					await this.Client.LogException(exception, e);
					response = string.Format(ErrorUnknownString, this.Client.GlobalConfig.AdminUserId);
				}

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

			newCommand = newCommand.CreateCopy("silentban");
			newCommand.Description = "Use with the same parameters like `ban`. The _reason_ message will not be sent to the user (hence silent.)";
			commands.Add(newCommand);

			newCommand = newCommand.CreateCopy("purgeban");
			newCommand.Description = "Use with the same parameters like `ban`. The difference is that this command will also delete all the messages of the user in last 24 hours.";
			commands.Add(newCommand);

// !quickban
			newCommand = new Command("quickban");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Quickly ban someone using pre-configured reason and duration, it also removes their messages. You can mention several people at once. (This command has to be first configured via `config` or <http://botwinder.info/config>.)";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					return;
				}

				if( string.IsNullOrEmpty(e.Server.Config.QuickbanReason) )
				{
					await e.Message.Channel.SendMessageSafe("This command has to be first configured via `config` or <http://botwinder.info/config>.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUsers = this.Client.GetMentionedUsersData(dbContext, e);

				if( mentionedUsers.Count == 0 )
				{
					await iClient.SendMessageToChannel(e.Channel, BanNotFoundString);
					dbContext.Dispose();
					return;
				}

				if( mentionedUsers.Count < e.MessageArgs.Length )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				string response = "ò_ó";

				try
				{
					response = await Ban(e.Server, mentionedUsers, TimeSpan.FromHours(e.Server.Config.QuickbanDuration), e.Server.Config.QuickbanReason, e.Message.Author as SocketGuildUser);
					dbContext.SaveChanges();
				} catch(Exception exception)
				{
					await this.Client.LogException(exception, e);
					response = string.Format(ErrorUnknownString, this.Client.GlobalConfig.AdminUserId);
				}

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

// !unban
			newCommand = new Command("unban");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Use with parameter `@user` where `@user` = user mention or id;";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUsers = this.Client.GetMentionedUsersData(dbContext, e);

				if( mentionedUsers.Count == 0 )
				{
					await iClient.SendMessageToChannel(e.Channel, BanNotFoundString);
					dbContext.Dispose();
					return;
				}

				if( mentionedUsers.Count < e.MessageArgs.Length )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				string response = "ó_ò";

				try
				{
					response = await UnBan(e.Server, mentionedUsers, e.Message.Author as SocketGuildUser);
					dbContext.SaveChanges();
				} catch(Exception exception)
				{
					await this.Client.LogException(exception, e);
					response = string.Format(ErrorUnknownString, this.Client.GlobalConfig.AdminUserId);
				}

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

// !addWarning
			newCommand = new Command("addWarning");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Use with parameters `@user warning` where `@user` = user mention or id, you can add the same warning to multiple people, just mention them all; `warning` = worded description, a warning message to store in the database.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await iClient.SendMessageToChannel(e.Channel, "Give a warning to whom?\n" + e.Command.Description);
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUsers = this.Client.GetMentionedUsersData(dbContext, e);

				if( mentionedUsers.Count == 0 )
				{
					await iClient.SendMessageToChannel(e.Channel, WarningNotFoundString);
					dbContext.Dispose();
					return;
				}

				if( mentionedUsers.Count + 1 > e.MessageArgs.Length )
				{
					await iClient.SendMessageToChannel(e.Channel, InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				StringBuilder warning = new StringBuilder();
				for(int i = mentionedUsers.Count; i < e.MessageArgs.Length; i++)
				{
					warning.Append(e.MessageArgs[i]);
					warning.Append(" ");
				}

				bool sendMessage = e.Command.Id.ToLower() == "issuewarning";
				foreach(UserData userData in mentionedUsers)
				{
					userData.AddWarning(warning.ToString());
					if( sendMessage )
					{
						try
						{
							SocketGuildUser user = e.Server.Guild.GetUser(userData.UserId);
							if( user != null )
								await user.SendMessageSafe(string.Format(WarningPmString,
									e.Server.Guild.Name, warning.ToString()));
						}
						catch(Exception) { }
					}
				}

				dbContext.SaveChanges();

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, "Done.");
			};
			commands.Add(newCommand);
			commands.Add(newCommand.CreateAlias("addwarning"));

			newCommand = newCommand.CreateCopy("issueWarning");
			newCommand.Description = "Use with parameters `@user warning` where `@user` = user mention or id, you can add the same warning to multiple people, just mention them all; `warning` = worded description, a warning message to store in the database. This will also be PMed to the user.";
			commands.Add(newCommand);
			commands.Add(newCommand.CreateAlias("issuewarning"));

// !removeWarning
			newCommand = new Command("removeWarning");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Use with parameter `@user` = user mention or id, you can remove the last warning from multiple people, just mention them all.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await iClient.SendMessageToChannel(e.Channel, "Remove warning from whom?\n" + e.Command.Description);
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUsers = this.Client.GetMentionedUsersData(dbContext, e);

				if( mentionedUsers.Count == 0 )
				{
					await iClient.SendMessageToChannel(e.Channel, WarningNotFoundString);
					dbContext.Dispose();
					return;
				}

				if( mentionedUsers.Count < e.MessageArgs.Length )
				{
					await e.Message.Channel.SendMessageSafe(InvalidArgumentsString + e.Command.Description);
					dbContext.Dispose();
					return;
				}

				bool allWarnings = e.Command.Id.ToLower() == "removeallwarnings";

				bool modified = false;
				foreach(UserData userData in mentionedUsers)
				{
					if( userData.WarningCount == 0 )
						continue;

					modified = true;
					if( allWarnings )
					{
						userData.WarningCount = 0;
						userData.Notes = "";
					}
					else
					{
						userData.WarningCount--;
						if( userData.Notes.Contains(" | ") )
						{
							int index = userData.Notes.LastIndexOf(" |");
							userData.Notes = userData.Notes.Remove(index);
						}
						else
						{
							userData.Notes = "";
						}
					}
				}

				if( modified )
					dbContext.SaveChanges();

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, "Done.");
			};
			commands.Add(newCommand);
			commands.Add(newCommand.CreateAlias("removewarning"));

			newCommand = newCommand.CreateCopy("removeAllWarnings");
			newCommand.Description = "Use with parameter `@user` = user mention or id, you can remove all the warnings from multiple people, just mention them all.";
			commands.Add(newCommand);
			commands.Add(newCommand.CreateAlias("removeallwarnings"));

// !whois
			newCommand = new Command("whois");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Search for a User on this server.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await iClient.SendMessageToChannel(e.Channel, "Who are you looking for?");
					return;
				}

				if( e.Server?.Guild?.Users == null )
				{
					await iClient.SendMessageToChannel(e.Channel, "Encountered unexpected D.Net library error.");
					return;
				}

				string expression = e.TrimmedMessage.ToLower();
				List<guid> foundUserIds = e.Server.Guild.Users
					.Where(u => u != null && ((u.Username != null && u.Username.ToLower().Contains(expression)) ||
					            (u.Nickname != null && u.Nickname.Contains(expression))))
					.Select(u => u.Id).ToList();

				foundUserIds.AddRange(e.Message.MentionedUsers.Select(u => u.Id));

				for( int i = 0; i < e.MessageArgs.Length; i++ )
				{
					if( guid.TryParse(e.MessageArgs[i], out guid id) )
						foundUserIds.Add(id);
				}

				string response = "I found too many, please be more specific.";
				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);

				if( foundUserIds.Count <= 5 )
				{
					StringBuilder whoisStrings = new StringBuilder();
					for( int i = 0; i < foundUserIds.Count; i++ )
					{
						UserData userData = dbContext.GetOrAddUser(e.Server.Id, foundUserIds[i]);
						if( userData != null )
						{
							SocketGuildUser user = e.Server.Guild.GetUser(foundUserIds[i]);
							whoisStrings.AppendLine(userData.GetWhoisString(dbContext, user));
						}
					}

					response = whoisStrings.ToString();
					if( string.IsNullOrEmpty(response) )
					{
						response = "I did not find anyone.";
					}
				}

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

// !find
			newCommand = new Command("find");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Find a User in the database based on ID, or an expression search through all their previous usernames and nicknames.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await iClient.SendMessageToChannel(e.Channel, "Who are you looking for?");
					return;
				}

				string response = "I found too many, please be more specific.";
				string expression = e.TrimmedMessage.ToLower();
				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<Username> foundUsernames = dbContext.Usernames.Where(u => u.ServerId == e.Server.Id && u.Name.ToLower().Contains(expression)).ToList();
				List<Nickname> foundNicknames = dbContext.Nicknames.Where(u => u.ServerId == e.Server.Id && u.Name.ToLower().Contains(expression)).ToList();
				List<guid> foundUserIds = new List<guid>();

				for( int i = 0; i < e.MessageArgs.Length; i++ )
				{
					if( guid.TryParse(e.MessageArgs[i], out guid id) )
						foundUserIds.Add(id);
				}

				foreach( Username username in foundUsernames )
				{
					if( foundUserIds.Contains(username.UserId) )
						continue;

					foundUserIds.Add(username.UserId);
				}
				foreach( Nickname nickname in foundNicknames )
				{
					if( foundUserIds.Contains(nickname.UserId) )
						continue;

					foundUserIds.Add(nickname.UserId);
				}

				if( foundUserIds.Count <= 5 )
				{
					StringBuilder whoisStrings = new StringBuilder();
					for( int i = 0; i < foundUserIds.Count; i++ )
					{
						UserData userData = dbContext.GetOrAddUser(e.Server.Id, foundUserIds[i]);
						if( userData != null )
						{
							SocketGuildUser user = e.Server.Guild.GetUser(foundUserIds[i]);
							whoisStrings.AppendLine(userData.GetWhoisString(dbContext, user));
						}
					}

					response = whoisStrings.ToString();
				}

				if( string.IsNullOrEmpty(response) )
				{
					response = "I did not find anyone.";
				}

				dbContext.Dispose();
				await iClient.SendMessageToChannel(e.Channel, response);
			};
			commands.Add(newCommand);

// !tempChannel
			newCommand = new Command("tempChannel");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Creates a temporary voice channel. This channel will be destroyed when it becomes empty, with grace period of three minutes since it's creation.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrWhiteSpace(e.TrimmedMessage) )
				{
					await this.Client.SendMessageToChannel(e.Channel, "Please specify a name for the new temporary channel.");
					return;
				}

				string responseString = string.Format(TempChannelConfirmString, e.TrimmedMessage);

				try
				{
					RestVoiceChannel tempChannel = await e.Server.Guild.CreateVoiceChannelAsync(e.TrimmedMessage);

					ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
					ChannelConfig channel = dbContext.Channels.FirstOrDefault(c => c.ServerId == e.Server.Id && c.ChannelId == e.Channel.Id);
					if( channel == null )
					{
						channel = new ChannelConfig{
							ServerId = e.Server.Id,
							ChannelId = tempChannel.Id
						};

						dbContext.Channels.Add(channel);
					}

					channel.Temporary = true;
					dbContext.SaveChanges();
					dbContext.Dispose();
				}
				catch(Exception exception)
				{
					await this.Client.LogException(exception, e);
					responseString = string.Format(ErrorUnknownString, this.Client.GlobalConfig.AdminUserId);
				}
				await this.Client.SendMessageToChannel(e.Channel, responseString);
			};
			commands.Add(newCommand);

			return commands;
		}

//User Joined
		private async Task OnUserJoined(SocketGuildUser user)
		{
			IRole role;
			Server server;
			if( !this.Client.Servers.ContainsKey(user.Guild.Id) ||
			    (server = this.Client.Servers[user.Guild.Id]) == null ||
			    (role = server.Guild.GetRole(server.Config.MuteRoleId)) == null )
				return;

			ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
			UserData userData = dbContext.GetOrAddUser(user.Guild.Id, user.Id);

			if( userData.MutedUntil > DateTime.MinValue + TimeSpan.FromMinutes(1) )
			{
				try
				{
					if( userData.MutedUntil < DateTime.UtcNow )
						await UnMute(server, new List<UserData>{userData}, role, server.Guild.CurrentUser);
					else
						await user.AddRoleAsync(role);
				}
				catch(Exception) { }

				dbContext.SaveChanges();
			}

			dbContext.Dispose();
		}


//Update
		public async Task Update(IBotwinderClient iClient)
		{
			BotwinderClient client = iClient as BotwinderClient;
			ServerContext dbContext = ServerContext.Create(client.DbConnectionString);
			bool save = false;

			//Channels
			List<ChannelConfig> channelsToRemove = new List<ChannelConfig>();
			foreach( ChannelConfig channelConfig in dbContext.Channels.Where(c => c.Temporary || (c.MutedUntil > DateTime.MinValue + TimeSpan.FromMinutes(1) && c.MutedUntil < DateTime.UtcNow)) )
			{
				//Muted channels
				if( channelConfig.MutedUntil > DateTime.MinValue + TimeSpan.FromMinutes(1) && channelConfig.MutedUntil < DateTime.UtcNow )
				{
					await UnmuteChannel(channelConfig, client.DiscordClient.CurrentUser);
					save = true;
					continue;
				}

				//Temporary voice channels
				Server server;
				if( !channelConfig.Temporary ||
					!client.Servers.ContainsKey(channelConfig.ServerId) ||
				    (server = client.Servers[channelConfig.ServerId]) == null )
					continue;

				SocketGuildChannel channel = server.Guild.GetChannel(channelConfig.ChannelId);
				if( channel != null &&
				    channel.CreatedAt < DateTimeOffset.UtcNow - this.TempChannelDelay &&
				    !channel.Users.Any() )
				{
					try
					{
						await channel.DeleteAsync();
						channelConfig.Temporary = false;
						channelsToRemove.Add(channelConfig);
						save = true;
						continue;
					}
					catch(Exception) { }
				}
			}

			if( channelsToRemove.Any() )
				dbContext.Channels.RemoveRange(channelsToRemove);


			//Users
			foreach( UserData userData in dbContext.UserDatabase )
			{
				try
				{
					Server server;

					//Unban
					if( userData.BannedUntil > DateTime.MinValue + TimeSpan.FromMinutes(1) && userData.BannedUntil < DateTime.UtcNow &&
					    client.Servers.ContainsKey(userData.ServerId) && (server = client.Servers[userData.ServerId]) != null )
					{
						await UnBan(server, new List<UserData>{userData}, server.Guild.CurrentUser);
						save = true;

						//else: ban them if they're on the server - implement if the ID pre-ban doesn't work.
					}

					//Unmute
					IRole role;
					if( userData.MutedUntil > DateTime.MinValue + TimeSpan.FromMinutes(1) && userData.MutedUntil < DateTime.UtcNow &&
					    client.Servers.ContainsKey(userData.ServerId) && (server = client.Servers[userData.ServerId]) != null &&
					    (role = server.Guild.GetRole(server.Config.MuteRoleId)) != null )
					{
						await UnMute(server, new List<UserData>{userData}, role, server.Guild.CurrentUser);
						save = true;
					}
				}
				catch(Exception exception)
				{
					if( !(exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden) )
						await this.HandleException(exception, "Update Moderation", userData.ServerId);
				}
			}

			if( save )
				dbContext.SaveChanges();
			dbContext.Dispose();
		}



// Ban
		/// <summary> Ban the User - this will also ban them as soon as they join the server, if they are not there right now. </summary>
		/// <param name="duration">Use zero for permanent ban.</param>
		/// <param name="silent">Set to true to not PM the user information about the ban (time, server, reason)</param>
		public async Task<string> Ban(Server server, List<UserData> users, TimeSpan duration, string reason, SocketGuildUser bannedBy = null, bool silent = false, bool deleteMessages = false)
		{
			DateTime bannedUntil = DateTime.MaxValue;
			if( duration.TotalHours >= 1 )
				bannedUntil = DateTime.UtcNow + duration;
			else
				duration = TimeSpan.Zero;

			string durationString = GetDurationString(duration);

			string response = "";
			List<guid> banned = new List<guid>();
			foreach( UserData userData in users )
			{
				SocketGuildUser user = null;
				if( !silent && (user = server.Guild.GetUser(userData.UserId)) != null )
				{
					try
					{
						await user.SendMessageSafe(string.Format(BanPmString,
							durationString.ToString(), server.Guild.Name, reason));
						await Task.Delay(500);
					}
					catch(Exception) { }
				}

				try
				{
					if( this.Client.Events.LogBan != null )
						await this.Client.Events.LogBan(server, user?.GetUsername(), userData.UserId, reason, durationString.ToString(), bannedBy);

					string logMessage = $"Banned {durationString.ToString()} with reason: {reason.Replace("@everyone", "@-everyone").Replace("@here", "@-here")}";
					await server.Guild.AddBanAsync(userData.UserId, (deleteMessages ? 1 : 0), (bannedBy?.GetUsername() ?? "") + " " + logMessage);
					userData.BannedUntil = bannedUntil;
					userData.AddWarning(logMessage);
					banned.Add(userData.UserId);
				}
				catch(Exception exception)
				{
					if( exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden )
						response = ErrorPermissionHierarchyString;
					else
						throw;
				}
			}

			if( banned.Any() )
				response = string.Format(BanConfirmString, banned.ToMentions());

			return response;
		}

		public async Task Ban(Server server, UserData userData, TimeSpan duration, string reason, SocketGuildUser bannedBy = null, bool silent = false, bool deleteMessages = false)
		{
			DateTime bannedUntil = DateTime.MaxValue;
			if( duration.TotalHours >= 1 )
				bannedUntil = DateTime.UtcNow + duration;
			else
				duration = TimeSpan.Zero;

			string durationString = GetDurationString(duration);

			SocketGuildUser user = null;
			if( !silent && (user = server.Guild.GetUser(userData.UserId)) != null )
			{
				try
				{
					await user.SendMessageSafe(string.Format(BanPmString,
						durationString, server.Guild.Name, reason));
					await Task.Delay(500);
				}
				catch(Exception) { }
			}

			if( this.Client.Events.LogBan != null )
				await this.Client.Events.LogBan(server, user?.GetUsername(), userData.UserId, reason, durationString, bannedBy);

			await server.Guild.AddBanAsync(userData.UserId, (deleteMessages ? 1 : 0), reason);
			userData.BannedUntil = bannedUntil;
			userData.AddWarning($"Banned {durationString} with reason: {reason}");
		}

		public async Task<string> UnBan(Server server, List<UserData> users, SocketGuildUser unbannedBy = null)
		{
			string response = "";
			List<guid> unbanned = new List<guid>();
			foreach( UserData userData in users )
			{
				try
				{
					if( this.Client.Events.LogUnban != null )
						await this.Client.Events.LogUnban(server, null, userData.UserId, unbannedBy);

					await server.Guild.RemoveBanAsync(userData.UserId);

					userData.BannedUntil = DateTime.MinValue;
					unbanned.Add(userData.UserId);
				}
				catch(Exception exception)
				{
					if( exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden )
						response = ErrorPermissionHierarchyString;
					else
						throw;
				}
			}

			if( unbanned.Any() )
				response = string.Format(UnbanConfirmString, unbanned.ToMentions());

			return response;
		}


// Mute
		public async Task Mute(Server server, UserData userData, TimeSpan duration, IRole role, SocketGuildUser mutedBy = null)
		{
			DateTime mutedUntil = DateTime.UtcNow + (duration.TotalMinutes < 5 ? TimeSpan.FromMinutes(5) : duration);
			string durationString = GetDurationString(duration);

			SocketGuildUser user = server.Guild.GetUser(userData.UserId);
			await user.AddRoleAsync(role);

			userData.MutedUntil = mutedUntil;
			userData.AddWarning($"Muted {durationString}");

			SocketTextChannel logChannel;
			if( (logChannel = server.Guild.GetTextChannel(server.Config.MuteIgnoreChannelId)) != null )
				await logChannel.SendMessageSafe(string.Format(MuteIgnoreChannelString, $"<@{userData.UserId}>"));

			if( this.Client.Events.LogMute != null )
				await this.Client.Events.LogMute(server, user, durationString, mutedBy);
		}

		public async Task<string> Mute(Server server, List<UserData> users, TimeSpan duration, IRole role, SocketGuildUser mutedBy = null)
		{
			DateTime mutedUntil = DateTime.UtcNow + (duration.TotalMinutes < 5 ? TimeSpan.FromMinutes(5) : duration);
			string durationString = GetDurationString(duration);

			string response = "";
			List<guid> muted = new List<guid>();
			foreach( UserData userData in users )
			{
				try
				{
					SocketGuildUser user = server.Guild.GetUser(userData.UserId);
					await user.AddRoleAsync(role);

					userData.MutedUntil = mutedUntil;
					userData.AddWarning($"Muted {durationString}");
					muted.Add(userData.UserId);

					if( this.Client.Events.LogMute != null )
						await this.Client.Events.LogMute(server, user, durationString, mutedBy);
				}
				catch(Exception exception)
				{
					if( exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden )
						response = ErrorPermissionHierarchyString;
					else
						throw;
				}
			}

			string mentions = muted.ToMentions();
			if( muted.Any() )
				response = string.Format(MuteConfirmString, mentions);

			SocketTextChannel logChannel;
			if( (logChannel = server.Guild.GetTextChannel(server.Config.MuteIgnoreChannelId)) != null )
				await logChannel.SendMessageSafe(string.Format(MuteIgnoreChannelString, mentions));

			return response;
		}

		public async Task<string> UnMute(Server server, List<UserData> users, IRole role, SocketGuildUser unmutedBy = null)
		{
			string response = "";
			List<guid> unmuted = new List<guid>();
			foreach( UserData userData in users )
			{
				try
				{
					SocketGuildUser user = server.Guild.GetUser(userData.UserId);
					if(user == null)
						continue;

					await user.RemoveRoleAsync(role);

					userData.MutedUntil = DateTime.MinValue;
					unmuted.Add(userData.UserId);

					if( this.Client.Events.LogUnmute != null )
						await this.Client.Events.LogUnmute(server, user, unmutedBy);
				}
				catch(Exception exception)
				{
					if( exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden )
						response = ErrorPermissionHierarchyString;
					else
						throw;
				}
			}

			if( unmuted.Any() )
				response = string.Format(UnmuteConfirmString, unmuted.ToMentions());

			return response;
		}

		public async Task<string> MuteChannel(ChannelConfig channelConfig, TimeSpan duration, SocketGuildUser mutedBy = null)
		{
			Server server;
			if( !this.Client.Servers.ContainsKey(channelConfig.ServerId) || (server = this.Client.Servers[channelConfig.ServerId]) == null )
				throw new Exception("Server not found.");

			string response = MuteChannelConfirmString;
			try
			{
				IRole role = server.Guild.EveryoneRole;
				SocketGuildChannel channel = server.Guild.GetChannel(channelConfig.ChannelId);
				OverwritePermissions permissions = channel.GetPermissionOverwrite(role) ?? new OverwritePermissions();
				permissions = permissions.Modify(sendMessages: PermValue.Deny);
				await channel.AddPermissionOverwriteAsync(role, permissions);

				channelConfig.MutedUntil = DateTime.UtcNow + duration;

				if( this.Client.Events.LogMutedChannel != null )
					await this.Client.Events.LogMutedChannel(server, channel, GetDurationString(duration), mutedBy);
			}
			catch(Exception exception)
			{
				if( exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden )
					response = ErrorPermissionHierarchyString;
				else
					throw;
			}
			return response;
		}

		public async Task<string> UnmuteChannel(ChannelConfig channelConfig, SocketUser unmutedBy = null)
		{
			Server server;
			if( !this.Client.Servers.ContainsKey(channelConfig.ServerId) || (server = this.Client.Servers[channelConfig.ServerId]) == null )
				throw new Exception("Server not found.");

			string response = UnmuteChannelConfirmString;
			try
			{
				channelConfig.MutedUntil = DateTime.MinValue;

				IRole role = server.Guild.EveryoneRole;
				SocketGuildChannel channel = server.Guild.GetChannel(channelConfig.ChannelId);
				OverwritePermissions permissions = channel.GetPermissionOverwrite(role) ?? new OverwritePermissions();
				permissions = permissions.Modify(sendMessages: PermValue.Inherit);
				await channel.AddPermissionOverwriteAsync(role, permissions);

				if( this.Client.Events.LogUnmutedChannel != null )
					await this.Client.Events.LogUnmutedChannel(server, channel, unmutedBy);
			}
			catch(Exception exception)
			{
				if( exception is Discord.Net.HttpException ex && ex.HttpCode == System.Net.HttpStatusCode.Forbidden )
					response = ErrorPermissionHierarchyString;
				else
					throw;
			}
			return response;
		}

		private string GetDurationString(TimeSpan duration)
		{
			StringBuilder durationString = new StringBuilder();

			if( duration == TimeSpan.Zero )
				durationString.Append("permanently");
			else
			{
				durationString.Append("for ");
				if( duration.Days > 0 )
				{
					durationString.Append(duration.Days);
					durationString.Append(duration.Days == 1 ? " day" : " days");
					if( duration.Hours > 0 || duration.Minutes > 0 )
						durationString.Append(" and ");
				}
				if( duration.Hours > 0 )
				{
					durationString.Append(duration.Hours);
					durationString.Append(duration.Hours == 1 ? " hour" : " hours");
					if( duration.Minutes > 0 )
						durationString.Append(" and ");
				}
				if( duration.Minutes > 0 )
				{
					durationString.Append(duration.Minutes);
					durationString.Append(duration.Minutes == 1 ? " minute" : " minutes");
				}
			}

			return durationString.ToString();
		}
	}
}
