﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Abp.Configuration;
using Abp.Domain.Uow;
using Shesha.NHibernate.UoW;
using Shesha.Reflection;
using Shouldly;
using Xunit;

namespace Shesha.Tests.Settings
{
    public class SettingsAudit_Tests : SheshaNhTestBase
    {

        private readonly IUnitOfWorkManager _unitOfWorkManager;
        private readonly ISettingManager _settingManager;

        public SettingsAudit_Tests()
        {
            _unitOfWorkManager = Resolve<IUnitOfWorkManager>();
            _settingManager = Resolve<ISettingManager>();
        }

        [Fact]
        public async Task Save_Test()
        {
            LoginAsHostAdmin();

            using (var uow = _unitOfWorkManager.Begin())
            {
                var nhuow = uow as NhUnitOfWork;
                var session = nhuow?.GetSession();

                var testSetting = _settingManager.GetSettingValue("TestSetting");
                testSetting.ShouldNotBeNull();

                _settingManager.ChangeSettingForApplication("TestSetting", "test 1");
                _settingManager.ChangeSettingForApplication("TestSetting", "test 2");
                try
                {
                    session.CreateSQLQuery("select count(1) from AbpSettings where Name = 'TestSetting'")
                        .UniqueResult<int>().ShouldBe(1);
                    session.CreateSQLQuery("select count(1) from AbpEntityPropertyChanges where EntityChangeId in (select id from AbpEntityChanges where EntityId = (select cast(id as nvarchar(max)) from AbpSettings where Name = 'TestSetting'))")
                        .UniqueResult<int>().ShouldBe(1);
                    session.CreateSQLQuery("select count(1) from AbpEntityChanges where EntityId in (select cast(id as nvarchar(max)) from AbpSettings where Name = 'TestSetting')")
                        .UniqueResult<int>().ShouldBe(2);
                    session.CreateSQLQuery("select count(1) from AbpEntityChangeSets where id in (select EntityChangeSetId from AbpEntityChanges where EntityId in (select cast(id as nvarchar(max)) from AbpSettings where Name = 'TestSetting'))")
                        .UniqueResult<int>().ShouldBe(2);
                }
                finally
                {
                    // delete temporary values
                    var entityChangeSetId = 
                        session.CreateSQLQuery("select EntityChangeSetId from AbpEntityChanges where EntityId in (select cast(id as nvarchar(max)) from AbpSettings where Name = 'TestSetting')")
                            .List<Int64>();

                    session.CreateSQLQuery($"delete from AbpEntityPropertyChanges where EntityChangeId in (select id from AbpEntityChanges where EntityId in (select cast(id as nvarchar(max)) from AbpSettings where Name = 'TestSetting'))")
                        .ExecuteUpdate();
                    session.CreateSQLQuery($"delete from AbpEntityChanges where EntityId in (select cast(id as nvarchar(max)) from AbpSettings where Name = 'TestSetting')")
                        .ExecuteUpdate();
                    foreach (var id in entityChangeSetId)
                    {
                        session.CreateSQLQuery($"delete from AbpEntityChangeSets where id = {id}")
                            .ExecuteUpdate();
                    }
                    session.CreateSQLQuery($"delete from AbpSettings where Name = 'TestSetting'")
                        .ExecuteUpdate();

                    session.Flush();
                }
            }
        }
    }
}
