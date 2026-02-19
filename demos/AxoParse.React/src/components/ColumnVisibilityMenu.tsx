import {Button, Checkbox, Menu} from '@mantine/core'
import type {VisibilityState} from '@tanstack/react-table'

interface ColumnDef {
    id: string
    header: string
}

interface ColumnVisibilityMenuProps {
    columns: ColumnDef[]
    visibility: VisibilityState
    onVisibilityChange: (updater: (prev: VisibilityState) => VisibilityState) => void
}

export function ColumnVisibilityMenu({columns, visibility, onVisibilityChange}: ColumnVisibilityMenuProps) {
    return (
        <Menu shadow="md" width={200}>
            <Menu.Target>
                <Button size="xs" variant="subtle" color="gray">Columns</Button>
            </Menu.Target>
            <Menu.Dropdown>
                {columns.map((col) => {
                    const visible = visibility[col.id] !== false
                    return (
                        <Menu.Item
                            key={col.id}
                            closeMenuOnClick={false}
                            leftSection={
                                <Checkbox
                                    size="xs"
                                    checked={visible}
                                    onChange={() => {
                                        onVisibilityChange((prev) => ({
                                            ...prev,
                                            [col.id]: !visible,
                                        }))
                                    }}
                                />
                            }
                        >
                            {col.header}
                        </Menu.Item>
                    )
                })}
            </Menu.Dropdown>
        </Menu>
    )
}
