import { memo, useCallback, useEffect, useRef, useState, type ChangeEvent } from "react"
import styles from "./simple-dropdown.module.css"
import { classNames } from "~/utils/styling";

export type SimpleDropdownProps = {
    type?: "plain" | "bordered"
    options: string[],
    value?: string,
    onChange?: (value: string) => void,
    valueRef?: React.RefObject<string>,
    ariaLabel?: string,
}

export const SimpleDropdown = memo(({ type, options, value, onChange, valueRef, ariaLabel }: SimpleDropdownProps) => {
    // validation
    if (!valueRef && (!value || !onChange)) {
        throw new Error("SimpleDropdown requires either the valueRef prop or both the value and onChange props.")
    }

    // state variables
    const [internalValue, setInternalValue] = useState(options.length > 0 ? options[0] : "");
    const [isOpen, setIsOpen] = useState(false);
    const [openAbove, setOpenAbove] = useState(false);
    const dropdownRef = useRef<HTMLDivElement>(null);

    // derived variables
    const renderedValue = value || internalValue;
    const containerClassNames = classNames([
        styles.container,
        type === "bordered" && styles.bordered
    ]);

    // events
    const toggleDropdown = useCallback(() => {
        if (!isOpen && dropdownRef.current) {
            const rect = dropdownRef.current.getBoundingClientRect();
            const viewportHeight = window.innerHeight;
            setOpenAbove(rect.top > viewportHeight / 2);
        }
        setIsOpen(prev => !prev);
    }, [isOpen]);

    const handleSelectedOptionChange = useCallback((option: string) => {
        if (!!valueRef) {
            setInternalValue(option);
            valueRef.current = option;
        }
        else if (!!onChange) {
            onChange(option);
        }
    }, [valueRef, setInternalValue, onChange]);

    const handleOptionClick = useCallback((option: string) => {
        handleSelectedOptionChange(option);
        setIsOpen(false);
    }, [handleSelectedOptionChange]);

    const handleSelectedKeyDown = useCallback((e: React.KeyboardEvent<HTMLDivElement>) => {
        if (e.key !== "Enter" && e.key !== " ") return;
        e.preventDefault();
        toggleDropdown();
    }, [toggleDropdown]);

    const handleOptionKeyDown = useCallback((option: string, e: React.KeyboardEvent<HTMLDivElement>) => {
        if (e.key !== "Enter" && e.key !== " ") return;
        e.preventDefault();
        handleOptionClick(option);
    }, [handleOptionClick]);

    const handleNativeChange = useCallback((e: ChangeEvent<HTMLSelectElement>) => {
        handleSelectedOptionChange(e.target.value);
    }, [handleSelectedOptionChange]);

    // close dropdown when clicking outside
    useEffect(() => {
        const handleClickOutside = (event: MouseEvent) => {
            if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
                setIsOpen(false);
            }
        };

        if (isOpen) {
            document.addEventListener('mousedown', handleClickOutside);
        }

        return () => {
            document.removeEventListener('mousedown', handleClickOutside);
        };
    }, [isOpen]);

    // view
    return (
        <div className={containerClassNames} ref={dropdownRef}>
            {/* hidden native select for mobile devices */}
            <select className={styles.nativeSelect} value={renderedValue} onChange={handleNativeChange} aria-label={ariaLabel}>
                {options.map(option => (
                    <option key={option} value={option}>{option}</option>
                ))}
            </select>

            {/* styled visible dropdown box */}
            <div
                className={styles.selected}
                onClick={toggleDropdown}
                onKeyDown={handleSelectedKeyDown}
                role="button"
                tabIndex={0}
                aria-label={ariaLabel}
                aria-haspopup="listbox"
                aria-expanded={isOpen}>
                {renderedValue}
                <span className={styles.arrow}></span>
            </div>

            {/* styled dropdown selection options for desktop devices */}
            {isOpen && (
                <div className={classNames([styles.dropdown, openAbove && styles.openAbove])} role="listbox" aria-label={ariaLabel}>
                    {options.map(option => (
                        <div
                            key={option}
                            className={styles.option}
                            onClick={() => handleOptionClick(option)}
                            onKeyDown={(e) => handleOptionKeyDown(option, e)}
                            role="option"
                            tabIndex={0}
                            aria-selected={option === renderedValue}>
                            {option}
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
});
