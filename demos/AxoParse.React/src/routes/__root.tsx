import {AppShell, Group, Title} from '@mantine/core'
import {NavigationProgress} from '@mantine/nprogress'
import {createRootRoute, Outlet} from '@tanstack/react-router'
import {TanStackRouterDevtools} from '@tanstack/react-router-devtools'

function RootLayout() {
    return (
        <AppShell header={{height: 56}} padding="md">
            <NavigationProgress/>
            <AppShell.Header>
                <Group h="100%" px="md">
                    <Title order={3}>AxoParse</Title>
                </Group>
            </AppShell.Header>
            <AppShell.Main style={{display: 'flex', flexDirection: 'column', height: 'calc(100vh - 56px)'}}>
                <Outlet/>
            </AppShell.Main>
            <TanStackRouterDevtools/>
        </AppShell>
    )
}

export const Route = createRootRoute({component: RootLayout})
