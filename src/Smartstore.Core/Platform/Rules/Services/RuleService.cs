﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Dasync.Collections;
using Microsoft.EntityFrameworkCore;
using Smartstore.Core.Data;
using Smartstore.Domain;

namespace Smartstore.Core.Rules
{
    public partial class RuleService : IRuleService
    {
        private readonly SmartDbContext _db;

        public RuleService(SmartDbContext db)
        {
            _db = db;
        }

        public virtual async Task<bool> ApplyRuleSetMappingsAsync<T>(T entity, int[] selectedRuleSetIds) 
            where T : BaseEntity, IRulesContainer
        {
            Guard.NotNull(entity, nameof(entity));

            selectedRuleSetIds ??= Array.Empty<int>();
            var updated = false;
            var allRuleSets = await _db.RuleSets
                .AsQueryable() // Prevent ambigous ext method call
                .Where(x => !x.IsSubGroup)
                .ToDictionaryAsync(x => x.Id);

            foreach (var ruleSetId in allRuleSets.Keys)
            {
                if (selectedRuleSetIds.Contains(ruleSetId))
                {
                    if (!entity.RuleSets.Any(x => x.Id == ruleSetId))
                    {
                        entity.RuleSets.Add(allRuleSets[ruleSetId]);
                        updated = true;
                    }
                }
                else if (entity.RuleSets.Any(x => x.Id == ruleSetId))
                {
                    entity.RuleSets.Remove(allRuleSets[ruleSetId]);
                    updated = false;
                }
            }

            return updated;
        }

        public async Task<IRuleExpressionGroup> CreateExpressionGroupAsync(int ruleSetId, IRuleVisitor visitor, bool includeHidden = false)
        {
            if (ruleSetId <= 0)
                return null;

            // TODO: prevent stack overflow > check if nested groups reference each other.

            var ruleSet = await _db.RuleSets
                .AsNoTracking()
                .Include(x => x.Rules)
                .FirstOrDefaultAsync(x => x.Id == ruleSetId);

            if (ruleSet == null)
            {
                // TODO: ErrHandling (???)
                return null;
            }

            return await CreateExpressionGroupAsync(ruleSet, visitor, includeHidden);
        }

        public virtual async Task<IRuleExpressionGroup> CreateExpressionGroupAsync(RuleSetEntity ruleSet, IRuleVisitor visitor, bool includeHidden = false)
        {
            if (ruleSet.Scope != visitor.Scope)
            {
                throw new SmartException($"Differing rule scope {ruleSet.Scope}. Expected {visitor.Scope}.");
            }

            if (!includeHidden && !ruleSet.IsActive)
            {
                return null;
            }

            var group = visitor.VisitRuleSet(ruleSet);

            var expressions = await ruleSet.Rules
                .SelectAsync(x => CreateExpression(x, visitor))
                .Where(x => x != null)
                .AsyncToArray();

            group.AddExpressions(expressions);

            return group;
        }

        private async Task<IRuleExpressionGroup> CreateExpressionGroup(RuleSetEntity ruleSet, RuleEntity refRule, IRuleVisitor visitor)
        {
            if (ruleSet.Scope != visitor.Scope)
            {
                // TODO: ErrHandling (ruleSet is for a different scope)
                return null;
            }

            var group = visitor.VisitRuleSet(ruleSet);
            if (refRule != null)
            {
                group.RefRuleId = refRule.Id;
            }

            var expressions = await ruleSet.Rules
                .SelectAsync(x => CreateExpression(x, visitor))
                .Where(x => x != null)
                .AsyncToArray();

            group.AddExpressions(expressions);

            return group;
        }

        private async Task<IRuleExpression> CreateExpression(RuleEntity ruleEntity, IRuleVisitor visitor)
        {
            if (!ruleEntity.IsGroup)
            {
                return await visitor.VisitRuleAsync(ruleEntity);
            }

            // It's a group, do recursive call.
            var group = await CreateExpressionGroupAsync(ruleEntity.Value.Convert<int>(), visitor);
            if (group != null)
            {
                group.RefRuleId = ruleEntity.Id;
            }

            return group;
        }
    }
}
