using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private ResourceAuthorityStore _groupAuthority;

        public IReadOnlyList<ChatGroup> ReadChatGroups(ChatSession scope)
        { return CaptureChatGroups(scope).Groups; }

        public ChatGroupSnapshot CaptureChatGroups(ChatSession scope)
        {
            var authority = DocumentArtifactStore.PlanScope(scope);
            var snapshot = _groupAuthority.Capture(authority);
            var groups = ReadGroups(authority, snapshot).Select(group => {
                group.Members = group.Members.Where(id => File.Exists(GetSessionPath(scope.Host, scope.DocumentKey, id))).ToList();
                return group;
            }).OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new ChatGroupSnapshot { Generation = snapshot.Generation, Groups = groups };
        }

        private List<ChatGroup> ReadGroups(ResourceAuthorityScopeId scope, ResourceAuthoritySnapshot snapshot)
        {
            var prefix = ResourceUri.Create("state", scope.Kind, scope.Id, "chat_group_");
            var groups = new List<ChatGroup>();
            foreach (var head in snapshot.Heads.Values.Where(item => item.Identity.Uri.StartsWith(prefix, StringComparison.Ordinal)))
            {
                if (head.Knowledge == HeadKnowledge.Removed) continue;
                if (head.Knowledge != HeadKnowledge.Known) throw new InvalidDataException("Состояние группы недоступно; запись запрещена.");
                var payload = _groupAuthority.GetRevision(scope, head.Revision)?.Payload;
                if (payload == null || payload.ByteLength > 1024 * 1024) throw new InvalidDataException("Недоступны данные группы.");
                var group = JsonConvert.DeserializeObject<ChatGroup>(_blobs.ReadText(payload.ToBlobReference()));
                if (group == null || group.Members == null || GroupIdentity(scope, group.Id).Uri != head.Identity.Uri ||
                    group.Revision != head.Revision.Revision) throw new InvalidDataException("Некорректная запись группы.");
                groups.Add(group);
            }
            return groups;
        }

        private static ResourceIdentity GroupIdentity(ResourceAuthorityScopeId scope, string id)
        {
            Guid parsed;
            if (id == null || !id.StartsWith("cg_", StringComparison.Ordinal) || !Guid.TryParseExact(id.Substring(3), "N", out parsed))
                throw new ArgumentException("Некорректный адрес группы.");
            return new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, "chat_group_" + id));
        }

        public ChatGroupChangeResult ChangeChatGroup(ChatSession scope, ChatGroupChange change, string operationId, Action dispatch = null)
        {
            if (change == null || !Enum.IsDefined(typeof(ChatGroupAction), change.Action) || string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Требуется команда группы.");
            var authority = DocumentArtifactStore.PlanScope(scope);
            // Same lock/order as chat deletion: an add cannot commit a deleted target.
            using (AcquirePersistenceRead())
            using (AcquireDocumentLock(scope.Host, scope.DocumentKey))
            {
                var snapshot = _groupAuthority.Capture(authority);
                var groups = ReadGroups(authority, snapshot);
                var create = change.Action == ChatGroupAction.Create;
                var groupId = create ? "cg_" + TextPatternEngine.Sha256(operationId).Substring(0, 32) : change.GroupId;
                var identity = GroupIdentity(authority, groupId);
                var head = snapshot.GetHead(identity);
                var group = groups.SingleOrDefault(item => item.Id == groupId);
                var name = (change.Name ?? "").Trim();
                if (create || change.Action == ChatGroupAction.Rename)
                {
                    if (name.Length == 0 || name.Length > 120 || name.Any(char.IsControl)) throw new ArgumentException("Название группы: одна строка, 1–120 символов.");
                    if (groups.Any(item => item.Id != groupId && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                        throw new ChatConcurrencyException("Группа с таким названием уже существует.");
                }
                if (create)
                {
                    if (head != null)
                    {
                        if (group?.CreationKey == operationId && group.Name == name)
                            return new ChatGroupChangeResult { Group = group };
                        throw new ChatConcurrencyException("Эта операция создания уже использована; удалённая группа не восстанавливается.");
                    }
                    group = new ChatGroup { Id = groupId, Name = name, CreationKey = operationId };
                }
                else if (group == null || string.IsNullOrEmpty(change.ExpectedRevision) || group.Revision != change.ExpectedRevision)
                    throw new ChatConcurrencyException("Группа изменилась или удалена. Обновите список.");

                var before = JsonConvert.SerializeObject(group);
                var members = change.Members ?? new string[0];
                if (change.Action == ChatGroupAction.Add || change.Action == ChatGroupAction.Remove)
                {
                    if (members.Length == 0 || members.Length > 50 || members.Any(string.IsNullOrWhiteSpace))
                        throw new ArgumentException("Укажите от 1 до 50 чатов.");
                    if (change.Action == ChatGroupAction.Add && members.Any(id => !File.Exists(GetSessionPath(scope.Host, scope.DocumentKey, id))))
                        throw new ChatConcurrencyException("Добавляемый чат удалён или недоступен в этой области.");
                    group.Members = (change.Action == ChatGroupAction.Add ? group.Members.Concat(members)
                        : group.Members.Except(members, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
                }
                if (change.Action == ChatGroupAction.Rename) group.Name = name;
                if (!create && change.Action != ChatGroupAction.Delete && before == JsonConvert.SerializeObject(group))
                    return new ChatGroupChangeResult { Group = group };
                ResourceHeadState after;
                if (change.Action == ChatGroupAction.Delete)
                    after = new ResourceHeadState(identity, HeadKnowledge.Removed, null, "chat-group-deleted", snapshot.Generation + 1);
                else
                {
                    group.Revision = "r_" + Guid.NewGuid().ToString("N");
                    var json = JsonConvert.SerializeObject(group);
                    if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new InvalidOperationException("Данные группы превышают 1 МиБ.");
                    var payload = PayloadRef.FromBlob(_blobs.StoreText(json, "application/json"));
                    var revision = new ResourceRef(identity.Uri, group.Revision);
                    _groupAuthority.RegisterRevision(authority, new ResourceRevisionMetadata(revision, payload.Sha256, payload, head?.Revision));
                    after = ResourceHeadState.Known(revision, snapshot.Generation + 1, "chat-group-changed");
                }
                dispatch?.Invoke();
                _groupAuthority.Publish(ResourceAuthorityCommit.Create(authority, snapshot.Generation, null,
                    new[] { new ResourceHeadChange(identity, head, after) }, AuthorityCommitReason.MetadataTransition));
                var actual = _groupAuthority.GetHead(authority, identity);
                if (!after.SameAuthority(actual)) throw new ChatConcurrencyException("Изменение группы не подтверждено. Обновите список без повтора команды.");
                return new ChatGroupChangeResult { Changed = true, Group = change.Action == ChatGroupAction.Delete ? null : group };
            }
        }
    }
}
