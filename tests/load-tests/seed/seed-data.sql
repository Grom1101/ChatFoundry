-- Connect to foundry_db_workflow and seed initial channel, bot, and branching workflow
\connect foundry_db_workflow;

-- Create tables IF NOT EXISTS in case seed runs before WorkflowService EnsureCreated
CREATE TABLE IF NOT EXISTS "MessengerChannels" (
    "Id" UUID PRIMARY KEY,
    "CreatedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "ModifiedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "Name" VARCHAR(200) NOT NULL,
    "Token" VARCHAR(500) NOT NULL,
    "ChannelType" INT NOT NULL,
    "CreatedUserId" UUID NOT NULL,
    "CompanyId" UUID NULL
);

CREATE TABLE IF NOT EXISTS "Bots" (
    "Id" UUID PRIMARY KEY,
    "CreatedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "ModifiedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "Name" VARCHAR(200) NOT NULL,
    "CreatedUserId" UUID NOT NULL,
    "CompanyId" UUID NULL
);

CREATE TABLE IF NOT EXISTS "BotChannels" (
    "BotId" UUID NOT NULL,
    "ChannelId" UUID NOT NULL,
    PRIMARY KEY ("BotId", "ChannelId")
);

CREATE TABLE IF NOT EXISTS "Workflows" (
    "Id" UUID PRIMARY KEY,
    "CreatedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "ModifiedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "BotId" UUID NOT NULL,
    "NodesDefinition" JSONB NOT NULL,
    "EdgesDefinition" JSONB NOT NULL,
    "LayoutDefinition" JSONB NOT NULL DEFAULT '[]',
    "Version" INT NOT NULL DEFAULT 1,
    "IsActiveBotWorkflow" BOOLEAN NOT NULL DEFAULT FALSE,
    "InputParametersDefinition" JSONB NOT NULL DEFAULT '[]',
    "OutputParametersDefinition" JSONB NOT NULL DEFAULT '[]'
);

CREATE TABLE IF NOT EXISTS "Sessions" (
    "Id" UUID PRIMARY KEY,
    "CreatedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "ModifiedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "WorkflowId" UUID NOT NULL,
    "ClientId" VARCHAR(200) NOT NULL,
    "Channel" INT NOT NULL,
    "ChannelId" UUID NOT NULL,
    "CurrentNodeId" UUID NULL,
    "Status" INT NOT NULL,
    "CompletedAt" TIMESTAMP WITH TIME ZONE NULL,
    "ParentSessionId" UUID NULL,
    "ParentActionId" UUID NULL,
    "Depth" INT NOT NULL DEFAULT 0,
    "Variables" TEXT NOT NULL DEFAULT '{}',
    "BotWorkflowId" UUID NULL
);

CREATE TABLE IF NOT EXISTS "Actions" (
    "Id" UUID PRIMARY KEY,
    "CreatedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "ModifiedAt" TIMESTAMP WITH TIME ZONE NOT NULL,
    "SessionId" UUID NOT NULL,
    "NodeId" UUID NOT NULL,
    "Payload" VARCHAR(1000) NULL,
    "MessageKind" INT NOT NULL,
    "WorkflowNodeType" INT NOT NULL,
    "Status" INT NOT NULL,
    "ErrorMessage" VARCHAR(2000) NULL
);

-- Insert or update MessengerChannel
INSERT INTO "MessengerChannels" ("Id", "CreatedAt", "ModifiedAt", "Name", "Token", "ChannelType", "CreatedUserId", "CompanyId")
VALUES (
    '00000000-0000-0000-0000-000000000001',
    NOW(),
    NOW(),
    'LoadTest Telegram Channel',
    '123456789:AAHk1234567890abcdefghijklmnopqr',
    0, -- Telegram
    '00000000-0000-0000-0000-000000000020',
    '00000000-0000-0000-0000-000000000010'
)
ON CONFLICT ("Id") DO UPDATE 
SET "Token" = EXCLUDED."Token", "Name" = EXCLUDED."Name";

-- Insert or update Bot
INSERT INTO "Bots" ("Id", "CreatedAt", "ModifiedAt", "Name", "CreatedUserId", "CompanyId")
VALUES (
    '00000000-0000-0000-0000-000000000002',
    NOW(),
    NOW(),
    'LoadTest Branching Bot',
    '00000000-0000-0000-0000-000000000020',
    '00000000-0000-0000-0000-000000000010'
)
ON CONFLICT ("Id") DO UPDATE 
SET "Name" = EXCLUDED."Name";

-- Link Bot to Channel
INSERT INTO "BotChannels" ("BotId", "ChannelId")
VALUES (
    '00000000-0000-0000-0000-000000000002',
    '00000000-0000-0000-0000-000000000001'
)
ON CONFLICT ("BotId", "ChannelId") DO NOTHING;

-- Insert or update BotWorkflow with branching: Start -> Ask (Buttons A, B) -> Message A or Message B
INSERT INTO "Workflows" (
    "Id",
    "CreatedAt",
    "ModifiedAt",
    "BotId",
    "NodesDefinition",
    "EdgesDefinition",
    "LayoutDefinition",
    "Version",
    "IsActiveBotWorkflow"
)
VALUES (
    '00000000-0000-0000-0000-000000000003',
    NOW(),
    NOW(),
    '00000000-0000-0000-0000-000000000002',
    '[
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "type": "Start",
            "label": "Start"
        },
        {
            "id": "22222222-2222-2222-2222-222222222222",
            "type": "Ask",
            "label": "Опрос с кнопками",
            "data": {
                "text": "Добро пожаловать в нагрузочный тест! Выберите ветку диалога:",
                "ui": {
                    "buttons": [
                        { "text": "Опция А", "value": "option_a" },
                        { "text": "Опция Б", "value": "option_b" }
                    ]
                }
            }
        },
        {
            "id": "33333333-3333-3333-3333-333333333333",
            "type": "Message",
            "label": "Ветка А",
            "data": {
                "text": "Вы выбрали Опцию А! Тест ветвления успешно пройден."
            }
        },
        {
            "id": "44444444-4444-4444-4444-444444444444",
            "type": "Message",
            "label": "Ветка Б",
            "data": {
                "text": "Вы выбрали Опцию Б! Тест ветвления успешно пройден."
            }
        }
    ]'::jsonb,
    '[
        {
            "from": "11111111-1111-1111-1111-111111111111",
            "to": "22222222-2222-2222-2222-222222222222"
        },
        {
            "from": "22222222-2222-2222-2222-222222222222",
            "to": "33333333-3333-3333-3333-333333333333",
            "condition": {
                "equals": {
                    "left": "$node.22222222-2222-2222-2222-222222222222.output",
                    "right": "option_a",
                    "ignoreCase": true
                }
            }
        },
        {
            "from": "22222222-2222-2222-2222-222222222222",
            "to": "44444444-4444-4444-4444-444444444444",
            "condition": {
                "equals": {
                    "left": "$node.22222222-2222-2222-2222-222222222222.output",
                    "right": "option_b",
                    "ignoreCase": true
                }
            }
        }
    ]'::jsonb,
    '[]'::jsonb,
    1,
    true
)
ON CONFLICT ("Id") DO UPDATE
SET "NodesDefinition" = EXCLUDED."NodesDefinition",
    "EdgesDefinition" = EXCLUDED."EdgesDefinition",
    "IsActiveBotWorkflow" = true;
