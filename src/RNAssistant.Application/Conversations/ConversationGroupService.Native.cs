using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Application.Conversations
{
    public sealed partial class ConversationGroupService
    {
        private readonly IConversationStore _conversations;
        private readonly ResourceAuthorityScopeId _scope;
        private readonly IResourceAuthorityStore _authority;
        private readonly IResourceRevisionStore _revisions;
        private readonly IResourceMutationJournal _mutations;
        private readonly ChatBlobStore _bodies;
        private readonly Func<ChatSession, ToolExecutionContext, ResourceMutationOrigin> _origin;

        public ConversationGroupService(IConversationStore conversations, ResourceAuthorityScopeId scope,
            IResourceAuthorityStore authority, IResourceRevisionStore revisions, IResourceMutationJournal mutations,
            ChatBlobStore bodies, Func<ChatSession, ToolExecutionContext, ResourceMutationOrigin> origin)
        {
            _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
            _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
            _bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
            _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        }

        private void RequireScope(ChatSession session)
        {
            if (!_scope.Equals(DocumentArtifactStore.PlanScope(session)) ||
                !string.IsNullOrEmpty(session.Id) && !Contains(session, session.Id))
                throw new ChatConcurrencyException("Область группы изменилась.");
        }

        private bool Contains(ChatSession session, string id)
        {
            var metadata = _conversations.ReadMetadata(id);
            return metadata != null && metadata.Host == session.Host && metadata.DocumentKey == session.DocumentKey &&
                metadata.WorkspaceId == session.WorkspaceId && metadata.DocumentAuthorityId == session.DocumentAuthorityId;
        }

        private ResourceIdentity GroupIdentity(string id)
        {
            Guid parsed;
            if (id == null || !id.StartsWith("cg_", StringComparison.Ordinal) || !Guid.TryParseExact(id.Substring(3), "N", out parsed))
                throw new ArgumentException("Некорректный адрес группы.");
            return new ResourceIdentity(ResourceUri.Create("state", _scope.Kind, _scope.Id, "chat_group_" + id));
        }

        private ChatGroupSnapshot CaptureNative(ChatSession session)
        {
            using (_mutations.AcquireScope(_scope, true))
            {
                RequireScope(session);
                var snapshot = ReadNativeGroups();
                foreach (var group in snapshot.Groups)
                    group.Members = group.Members.Where(id => Contains(session, id)).ToList();
                return snapshot;
            }
        }

        private ChatGroupSnapshot ReadNativeGroups()
        {
            var prefix = ResourceUri.Create("state", _scope.Kind, _scope.Id, "chat_group_");
            var ranges = new[] { new ResourceHeadRange(prefix, prefix + "\uffff") };
            var groups = new List<ChatGroup>();
            var offset = 0; long? generation = null;
            do
            {
                var page = _authority.ReadHeads(_scope, ranges, offset, 100);
                if (generation.HasValue && generation.Value != page.Generation)
                    throw new ChatConcurrencyException("Группы изменились. Обновите список.");
                generation = page.Generation;
                foreach (var head in page.Items)
                {
                    if (head.Knowledge == HeadKnowledge.Removed) continue;
                    if (head.Knowledge != HeadKnowledge.Known) throw new InvalidDataException("Состояние группы недоступно; запись запрещена.");
                    var payload = _revisions.GetRevision(_scope, head.Revision)?.Payload;
                    if (payload == null || payload.ByteLength > 1024 * 1024) throw new InvalidDataException("Недоступны данные группы.");
                    var group = JsonConvert.DeserializeObject<ChatGroup>(_bodies.ReadText(payload.ToBlobReference()));
                    if (group == null || group.Members == null || GroupIdentity(group.Id).Uri != head.Identity.Uri ||
                        group.Revision != head.Revision.Revision) throw new InvalidDataException("Некорректная запись группы.");
                    groups.Add(group);
                }
                if (!page.NextOffset.HasValue) break;
                offset = page.NextOffset.Value;
            } while (true);
            return new ChatGroupSnapshot { Generation = generation.Value,
                Groups = groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray() };
        }

        private ChatGroupChangeResult ChangeNative(ChatSession session, ChatGroupChange command, string operationId,
            Action dispatch, ToolExecutionContext execution)
        {
            if (command == null || !Enum.IsDefined(typeof(ChatGroupAction), command.Action) || string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Требуется команда группы.");
            // Native chat deletion must take this same scope lease when its Store port is connected.
            using (_mutations.AcquireScope(_scope))
            {
                RequireScope(session);
                var snapshot = ReadNativeGroups();
                var create = command.Action == ChatGroupAction.Create;
                var id = create ? "cg_" + TextPatternEngine.Sha256(operationId).Substring(0, 32) : command.GroupId;
                var identity = GroupIdentity(id);
                var head = _authority.GetHead(_scope, identity);
                var group = snapshot.Groups.SingleOrDefault(item => item.Id == id);
                var name = (command.Name ?? "").Trim();
                if (create || command.Action == ChatGroupAction.Rename)
                {
                    if (name.Length == 0 || name.Length > 120 || name.Any(char.IsControl)) throw new ArgumentException("Название группы: одна строка, 1–120 символов.");
                    if (snapshot.Groups.Any(item => item.Id != id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                        throw new ChatConcurrencyException("Группа с таким названием уже существует.");
                }
                if (create)
                {
                    if (head != null)
                    {
                        if (group?.CreationKey == operationId && group.Name == name) return new ChatGroupChangeResult { Group = group };
                        throw new ChatConcurrencyException("Эта операция создания уже использована; удалённая группа не восстанавливается.");
                    }
                    group = new ChatGroup { Id = id, Name = name, CreationKey = operationId };
                }
                else if (group == null || string.IsNullOrEmpty(command.ExpectedRevision) || group.Revision != command.ExpectedRevision)
                    throw new ChatConcurrencyException("Группа изменилась или удалена. Обновите список.");

                var before = JsonConvert.SerializeObject(group);
                if (command.Action == ChatGroupAction.Add || command.Action == ChatGroupAction.Remove)
                {
                    var members = command.Members ?? new string[0];
                    if (members.Length == 0 || members.Length > 50 || members.Any(string.IsNullOrWhiteSpace))
                        throw new ArgumentException("Укажите от 1 до 50 чатов.");
                    if (command.Action == ChatGroupAction.Add && members.Any(member => !Contains(session, member)))
                        throw new ChatConcurrencyException("Добавляемый чат удалён или недоступен в этой области.");
                    group.Members = (command.Action == ChatGroupAction.Add ? group.Members.Concat(members) :
                        group.Members.Except(members, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).OrderBy(member => member, StringComparer.Ordinal).ToList();
                }
                if (command.Action == ChatGroupAction.Rename) group.Name = name;
                if (!create && command.Action != ChatGroupAction.Delete && before == JsonConvert.SerializeObject(group))
                    return new ChatGroupChangeResult { Group = group };

                ResourceHeadState after; PayloadRef payload = null;
                if (command.Action == ChatGroupAction.Delete)
                    after = new ResourceHeadState(identity, HeadKnowledge.Removed, null, "chat-group-deleted", snapshot.Generation + 1);
                else
                {
                    group.Revision = "r_" + Guid.NewGuid().ToString("N");
                    var json = JsonConvert.SerializeObject(group);
                    if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new InvalidOperationException("Данные группы превышают 1 МиБ.");
                    payload = PayloadRef.FromBlob(_bodies.StoreText(json, "application/json"));
                    var revision = new ResourceRef(identity.Uri, group.Revision);
                    _revisions.RegisterRevision(_scope, new ResourceRevisionMetadata(revision, payload.Sha256, payload, head?.Revision));
                    after = ResourceHeadState.Known(revision, snapshot.Generation + 1, "chat-group-changed");
                }
                var origin = execution == null ? null : _origin(session, execution) ??
                    throw new InvalidOperationException("Требуется точный runtime origin изменения группы.");
                var impact = new ResourceImpact(identity, ResourceImpactRelation.Exact, before: head?.Revision, after: after.Revision);
                var attempt = _mutations.Prepare(_scope, AgentCommunicationToolCatalog.Group, identity, head?.Revision?.Revision,
                    payload, intendedImpacts: new[] { impact }, origin: origin);
                _mutations.MarkDispatchMayHaveOccurred(attempt.AttemptId);
                dispatch?.Invoke();
                var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), AgentCommunicationToolCatalog.Group,
                    ResourceEffectOutcome.VerifiedChanged, new[] { impact });
                var commit = ResourceAuthorityCommit.Create(_scope, snapshot.Generation, effect,
                    new[] { new ResourceHeadChange(identity, head, after) }, AuthorityCommitReason.MutationEffect, attempt.AttemptId);
                _authority.Publish(commit);
                if (!after.SameAuthority(_authority.GetHead(_scope, identity)))
                    throw new ChatConcurrencyException("Изменение группы не подтверждено. Обновите список без повтора команды.");
                _mutations.Resolve(attempt.AttemptId, commit.CommitId);
                return new ChatGroupChangeResult { Changed = true, Group = command.Action == ChatGroupAction.Delete ? null : group,
                    AuthorityCommit = commit };
            }
        }
    }
}
